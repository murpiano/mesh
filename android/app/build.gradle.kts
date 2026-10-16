plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("com.google.devtools.ksp")
}

android {
    namespace = "com.mesh"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.mesh"
        minSdk = 26
        targetSdk = 34
        // Release builds pass the tag's version: -PmeshVersion=1.2.3 gives versionName 1.2.3
        // and versionCode 10203, so every release installs over the previous one.
        val meshVersion = (findProperty("meshVersion") as String?) ?: "0.1.0"
        val (major, minor, patch) = meshVersion.split(".").map { it.toInt() }
        versionCode = major * 10000 + minor * 100 + patch
        versionName = meshVersion
    }

    // Checked into the repo on purpose: Android refuses to install an update signed by a
    // different certificate than what's already on the device ("App not installed" /
    // INSTALL_FAILED_UPDATE_INCOMPATIBLE), forcing an uninstall first. The default debug
    // signing config instead auto-generates a random keystore per machine, so a build from one
    // machine can never update an install from another. Pinning this one fixed debug keystore
    // for every debug build — mine and yours — means `adb install -r app-debug.apk` (or tapping
    // the APK again to "Update") always works, no reinstall needed. Debug-only: never used for
    // release signing, and the usual "debug keystore" risk profile (not secret, not meant to be).
    signingConfigs {
        getByName("debug") {
            storeFile = file("debug.keystore")
            storePassword = "android"
            keyAlias = "androiddebugkey"
            keyPassword = "android"
        }
        // The release key is never in the repo. CI writes it from a secret and points
        // MESH_KEYSTORE at it; without that variable the release APK stays unsigned.
        System.getenv("MESH_KEYSTORE")?.let { path ->
            create("release") {
                storeFile = file(path)
                storePassword = System.getenv("MESH_KEYSTORE_PASSWORD")
                keyAlias = "mesh"
                keyPassword = System.getenv("MESH_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.4")
    implementation("androidx.fragment:fragment-ktx:1.8.5")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("androidx.room:room-runtime:2.6.1")
    implementation("androidx.room:room-ktx:2.6.1")
    ksp("androidx.room:room-compiler:2.6.1")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.8.1")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.8.1")
}
