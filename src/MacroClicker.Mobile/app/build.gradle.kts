plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.macroclicker.mobile"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.macroclicker.mobile"
        minSdk = 26
        targetSdk = 35
        versionCode = 2
        versionName = "2.0.0"
    }

    buildFeatures {
        viewBinding = true
    }

    signingConfigs {
        create("release") {
            // CI 提交了固定密钥 release.keystore（保证每次构建签名一致，可覆盖安装）；
            // 本地无密钥环境时自动回退 debug 签名
            val ks = System.getenv("MC_KEYSTORE")
            if (!ks.isNullOrBlank()) {
                storeFile = rootProject.file(ks)
                storePassword = System.getenv("MC_STORE_PASS")
                keyAlias = System.getenv("MC_KEY_ALIAS") ?: "macroclicker"
                keyPassword = System.getenv("MC_KEY_PASS")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (!System.getenv("MC_KEYSTORE").isNullOrBlank()) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    lint {
        checkReleaseBuilds = false
        abortOnError = false
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
}
