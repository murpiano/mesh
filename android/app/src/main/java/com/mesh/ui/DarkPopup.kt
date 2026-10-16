package com.mesh.ui

import android.graphics.Color
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.PopupWindow
import android.widget.TextView
import com.mesh.R

/**
 * One shared look for every popup menu in the app (device long-press actions, a chat message's
 * Copy/Edit/Delete, the "+" attach menu): background darker than the screen behind it, muted
 * gray item text, thin dividers between items. See raw/design/1.jpg and 2.jpg.
 */
object DarkPopup {
    // Item color by meaning, not by position: affirmative/connect actions read green, destructive
    // or negative ones read red, everything else stays the shared muted gray. Also used by
    // PinConfirmDialog so every popup/dialog in the app colors its buttons the same way.
    private val POSITIVE = setOf("Connect", "Yes", "OK", "Save", "Allow", "Re-pair", "Submit")
    private val NEGATIVE = setOf("Disconnect", "Unpair", "Delete", "Cancel", "No", "Deny")

    // Every label any popup in the app can show. One shared item width is measured from this
    // whole set — not from whichever subset the current call passes — so a 1-item "Copy" popup
    // and a 3-item "Copy/Edit/Delete" popup are the same width as every device-action popup too.
    private val ALL_LABELS = POSITIVE + NEGATIVE + setOf("Copy", "Edit", "Image", "File")

    /** The one popup currently showing, if any. A [PopupWindow] is its own overlay window, not
     * part of the fragment it was shown from — swiping/navigating away (e.g. system Back from a
     * device page to Devices) destroys that fragment's view without ever touching this window,
     * so it would otherwise keep floating on screen. Fragments call [dismissCurrent] from
     * onDestroyView to close it when their view goes away. */
    private var currentPopup: PopupWindow? = null

    /** Closes the currently showing popup, if any — see [currentPopup]. Safe to call when none
     * is showing. */
    fun dismissCurrent() {
        currentPopup?.dismiss()
    }

    /** The color a button/menu item labelled [label] should read, by meaning rather than by
     * position (an "Unpair" button is destructive even sitting in a dialog's positive slot). */
    fun colorFor(label: String): Int = when (label) {
        in POSITIVE -> R.color.action_positive
        in NEGATIVE -> R.color.action_negative
        else -> R.color.text_muted
    }

    /** Shows a popup with one row per entry in [items], in order, centered on the screen
     * regardless of where the triggering tap landed. [onSelected] fires with the clicked item's
     * text. [onDismiss] fires once the popup closes either way (used to clear a
     * tap-highlighted row). Not focusable, so a tap on another row/message passes straight
     * through to that row's own click listener in the same gesture that dismisses this popup —
     * switching the open popup to a newly-tapped message takes one tap, not two. */
    fun show(
        anchor: View,
        items: List<String>,
        onDismiss: () -> Unit = {},
        onSelected: (String) -> Unit
    ) {
        val context = anchor.context
        val itemWidth = fixedItemWidthPx(context)
        val container = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            background = context.getDrawable(R.drawable.popup_background)
            clipToOutline = true
        }
        items.forEachIndexed { index, label ->
            if (index > 0) {
                container.addView(View(context).apply {
                    setBackgroundColor(context.getColor(R.color.divider_popup))
                }, LinearLayout.LayoutParams(itemWidth, dp(context, 1)))
            }
            container.addView(TextView(context).apply {
                text = label
                setTextColor(context.getColor(colorFor(label)))
                textSize = 15f
                gravity = Gravity.CENTER
                // Vertical padding is the original 13dp — height was never meant to change.
                // Horizontal padding doesn't control width any more: every item gets the same
                // fixed itemWidth below, regardless of its own text or which other items are in
                // this particular popup.
                setPadding(0, dp(context, 13), 0, dp(context, 13))
                background = context.getDrawable(R.drawable.popup_item_background)
                isClickable = true
            }, LinearLayout.LayoutParams(itemWidth, LinearLayout.LayoutParams.WRAP_CONTENT))
        }

        // focusable = false: lets a tap elsewhere dismiss this popup (via isOutsideTouchable)
        // WITHOUT swallowing that same tap, so it still reaches whatever's underneath — see the
        // doc comment above.
        val popup = PopupWindow(container, LinearLayout.LayoutParams.WRAP_CONTENT,
            LinearLayout.LayoutParams.WRAP_CONTENT, false)
        popup.isOutsideTouchable = true
        popup.setBackgroundDrawable(android.graphics.drawable.ColorDrawable(Color.TRANSPARENT))
        popup.elevation = dp(context, 8).toFloat()
        popup.setOnDismissListener {
            if (currentPopup === popup) currentPopup = null
            onDismiss()
        }

        currentPopup?.dismiss()
        currentPopup = popup

        for (i in 0 until container.childCount) {
            val child = container.getChildAt(i)
            if (child is TextView) {
                child.setOnClickListener {
                    popup.dismiss()
                    onSelected(child.text.toString())
                }
            }
        }

        popup.showAtLocation(anchor.rootView, Gravity.CENTER, 0, 0)
    }

    /** Widest label in [ALL_LABELS] at the shared 15sp item text size, plus the halved horizontal
     * padding (3dp each side) that items used to carry themselves — same width for every popup
     * in the app, independent of which labels any one call actually shows. */
    private fun fixedItemWidthPx(context: android.content.Context): Int {
        val probe = TextView(context).apply { textSize = 15f }
        val widestTextPx = ALL_LABELS.maxOf { probe.paint.measureText(it) }
        val base = widestTextPx.toInt() + dp(context, 3) * 2
        return (base * 1.5f).toInt()
    }

    private fun dp(context: android.content.Context, value: Int): Int =
        (value * context.resources.displayMetrics.density).toInt()
}
