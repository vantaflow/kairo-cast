import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.vantaflow.kairo.cast"
    compileSdk = 34
    ndkVersion = "27.2.12479018"

    defaultConfig {
        applicationId = "com.vantaflow.kairo.cast"
        minSdk = 30
        targetSdk = 34
        versionCode = 10000
        versionName = "1.0.0"
        ndk {
            abiFilters += listOf("armeabi-v7a", "arm64-v8a")
        }
        externalNativeBuild {
            cmake {
                arguments += listOf("-DANDROID_STL=c++_shared")
            }
        }
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
                // Par défaut : keystore de debug.
                val dbg = File(System.getProperty("user.home"), ".android/debug.keystore")
                storeFile = dbg
                storePassword = "android"
                keyAlias = "androiddebugkey"
                keyPassword = "android"
            }
        }
    }

    buildTypes {
        release {
            // R8 désactivé : le code appelé depuis JNI (NativeReceiver, ReceiverListener) est
            // référencé par nom depuis le C, et la taille de l'APK n'est pas critique.
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
        prefab = true
    }
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("com.android.ndk.thirdparty:openssl:1.1.1q-beta-1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.7.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3")
    testImplementation("junit:junit:4.13.2")
}
