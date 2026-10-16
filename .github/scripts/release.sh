#!/usr/bin/env bash
# Creates the next GitHub release from Conventional Commits since the last v* tag.
#
# Usage: bash .github/scripts/release.sh [--dry-run] [<commit>]
#   --dry-run  print the version and notes, create nothing
#   <commit>   commit to release, HEAD by default
#
# Bump: "!" or "BREAKING CHANGE:" gives major, feat gives minor, fix or perf gives patch.
# Other types (docs, chore, ci, test, refactor, style, build) do not produce a release.
# Needs GH_TOKEN for gh unless --dry-run is set.

set -euo pipefail

dry_run=0
target=HEAD
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=1 ;;
    *) target=$arg ;;
  esac
done
sha=$(git rev-parse "$target")

last=$(git describe --tags --abbrev=0 --match 'v[0-9]*.[0-9]*.[0-9]*' "$sha" 2>/dev/null || true)
if [ -z "$last" ]; then
  echo "No v* tag yet. Create the first release by hand." >&2
  exit 1
fi

bump=0 # 0 none, 1 patch, 2 minor, 3 major
breaking=() added=() fixed=()

entry() {
  local subject=$1 body=$2 paragraph
  # First paragraph of the body, without trailers, joined into one line.
  paragraph=$(printf '%s\n' "$body" | sed '/^[A-Za-z-]*: /d; /^BREAKING CHANGE/d' |
    awk 'NF {printf "%s%s", sep, $0; sep = " "; seen = 1; next} seen {exit}')
  if [ -n "$paragraph" ]; then
    printf -- '- %s\n  %s' "$subject" "$paragraph"
  else
    printf -- '- %s' "$subject"
  fi
}

while IFS= read -r -d $'\x1e' record; do
  record=${record#$'\n'}
  subject=${record%%$'\x1f'*}
  body=${record#*$'\x1f'}
  body=$(printf '%s' "$body" | sed 's/^[[:space:]]*//')
  pattern='^([a-z]+)(\([^)]*\))?(!)?: (.+)$'
  [[ "$subject" =~ $pattern ]] || continue
  type=${BASH_REMATCH[1]}
  bang=${BASH_REMATCH[3]}
  text=${BASH_REMATCH[4]}
  # Subjects start in lower case; notes start with a capital unless the word is a name (iOS).
  first=${text%% *}
  [[ ${first:1} =~ [[:upper:]] ]] || text=${text^}
  item=$(entry "$text" "$body")
  if [ -n "$bang" ] || grep -q '^BREAKING CHANGE' <<<"$body"; then
    breaking+=("$item")
    bump=3
    continue
  fi
  case "$type" in
    feat) added+=("$item"); ((bump < 2)) && bump=2 ;;
    fix | perf) fixed+=("$item"); ((bump < 1)) && bump=1 ;;
  esac
done < <(git log --no-merges --reverse --format='%s%x1f%b%x1e' "$last..$sha")

if [ "$bump" -eq 0 ]; then
  echo "No feat, fix, perf or breaking commits since $last. No release."
  exit 0
fi

IFS=. read -r major minor patch <<<"${last#v}"
case "$bump" in
  3) next="$((major + 1)).0.0" ;;
  2) next="$major.$((minor + 1)).0" ;;
  1) next="$major.$minor.$((patch + 1))" ;;
esac
tag="v$next"

notes=$(mktemp)
{
  section() {
    local title=$1
    shift
    [ "$#" -eq 0 ] && return 0
    printf '### %s\n\n' "$title"
    printf '%s\n' "$@"
    printf '\n'
  }
  section 'Breaking changes' "${breaking[@]}"
  section 'New' "${added[@]}"
  section 'Fixed' "${fixed[@]}"
  remote=$(git remote get-url origin 2>/dev/null | sed -E 's#^git@github\.com:#https://github.com/#; s#\.git$##')
  [ -n "$remote" ] && printf 'Full diff: %s/compare/%s...%s\n' "$remote" "$last" "$tag"
} >"$notes"

echo "$last -> $tag"
if [ "$dry_run" -eq 1 ]; then
  echo
  cat "$notes"
  exit 0
fi

if git ls-remote --exit-code --tags origin "refs/tags/$tag" >/dev/null 2>&1; then
  echo "$tag already exists. Nothing to do."
  exit 0
fi
gh release create "$tag" --target "$sha" --title "$tag" --notes-file "$notes"
