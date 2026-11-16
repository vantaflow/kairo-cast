plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

// Codec Kairo Link (trames KCST, HELLO), partagé par le récepteur TV (:app) et l'émetteur Android (:sender).
android {
    namespace = "com.vantaflow.kairo.cast.link"
    compileSdk = 34
    defaultConfig { minSdk = 29 }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}
