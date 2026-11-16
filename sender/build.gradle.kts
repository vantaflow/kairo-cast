import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Kairo Cast Envoi : partage l'écran et le son d'un téléphone ou d'une tablette Android vers une source de la box (Kairo Link).
android {
    testOptions { unitTests.isReturnDefaultValues = true }
    namespace = "com.vantaflow.kairo.cast.sender"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.vantaflow.kairo.cast.sender"
        minSdk = 29 // capture du son des applis (AudioPlaybackCapture)
        targetSdk = 34
        versionCode = 10000
        versionName = "1.0.0"
    }

    signingConfigs {
        create("release") {
            val props = Properties()
            val f = rootProject.file("keystore.properties")
            if (f.exists()) {
                f.inputStream().use { props.load(it) }
                storeFile = rootProject.file(props.getProperty("storeFile"))
                storePassword = props.getProperty("storePassword")
                keyAlias = props.getProperty("keyAlias")
                keyPassword = props.getProperty("keyPassword")
            } else {
                storeFile = File(System.getProperty("user.home"), ".android/debug.keystore")
                storePassword = "android"
                keyAlias = "androiddebugkey"
                keyPassword = "android"
            }
        }
    }

    buildTypes {
        debug {
            if (rootProject.file("keystore.properties").exists()) signingConfig = signingConfigs.getByName("release")
        }
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    implementation(project(":link"))
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
