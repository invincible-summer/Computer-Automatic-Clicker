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
        versionCode = 1
        versionName = "1.0.0"
    }

    signingConfigs {
        create("release") {
            // CI 中通过环境变量注入签名信息；本地未配置时退回 debug 签名
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
            if (!System.getenv("MC_KEYSTORE").isNullOrBlank()) {
                signingConfig = signingConfigs.getByName("release")
            } else {
                // 本地无签名环境时回退 debug 签名，保证 assembleRelease 产物可直接安装
                signingConfig = signingConfigs.getByName("debug")
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
        // CI 只需产出 APK，避免静态检查中断构建
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
