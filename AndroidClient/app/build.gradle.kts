// App-level build.gradle.kts for Tab Mirror Android Client

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "com.tabmirror"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.tabmirror"
        minSdk = 26              // Android 8.0 — MediaCodec low-latency API stable
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    buildFeatures {
        viewBinding = true
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
    // AndroidX core
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)
    implementation(libs.material)

    // Coroutines — for async networking without blocking the UI thread
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    // Lifecycle-aware coroutine scope in Activity
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
}
