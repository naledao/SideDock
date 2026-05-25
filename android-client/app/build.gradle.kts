plugins {
    id("com.android.application")
}

android {
    namespace = "com.sidedock.client"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.sidedock.client"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "0.1.0"
    }

    signingConfigs {
        create("release") {
            val storeFilePath = providers.environmentVariable("SIDEDOCK_RELEASE_STORE_FILE").orNull
            if (!storeFilePath.isNullOrBlank()) {
                storeFile = file(storeFilePath)
                storePassword = providers.environmentVariable("SIDEDOCK_RELEASE_STORE_PASSWORD").orNull
                keyAlias = providers.environmentVariable("SIDEDOCK_RELEASE_KEY_ALIAS").orNull
                keyPassword = providers.environmentVariable("SIDEDOCK_RELEASE_KEY_PASSWORD").orNull
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.getByName("release")
        }
    }
}
