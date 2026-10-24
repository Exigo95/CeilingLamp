plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The interface is shared with the Windows app: copy windows/CeilingLamp.Tray/Ui/app.html into the APK.
val copyUi = tasks.register<Copy>("copyUi") {
    from(rootProject.file("../windows/CeilingLamp.Tray/Ui/app.html"))
    into(layout.buildDirectory.dir("uiAssets"))
}
tasks.named("preBuild") { dependsOn(copyUi) }

android {
    namespace = "de.ceilinglight"
    compileSdk = 34

    defaultConfig {
        applicationId = "de.ceilinglight.app"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"
    }

    // One fixed key for every build (Android Studio and GitHub), so new versions install over old ones.
    signingConfigs {
        create("shared") {
            storeFile = file("ceilinglight.keystore")
            storePassword = "ceilinglight"
            keyAlias = "ceilinglight"
            keyPassword = "ceilinglight"
        }
    }

    buildTypes {
        debug { signingConfig = signingConfigs.getByName("shared") }
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("shared")
        }
    }

    sourceSets["main"].assets.srcDir(layout.buildDirectory.dir("uiAssets"))

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-ktx:1.9.2")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
}
