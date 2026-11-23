import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "com.rsxdevelops.extendspace"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "com.rsxdevelops.extendspace"
        minSdk = 29
        targetSdk = 37
        // D71: the same version as the Windows host (apps/windows/Directory.Build.props).
        // versionCode = major * 1_000_000 + minor * 10_000 + patch * 100 + pre-release number
        // (99 for a final release), so every beta and the release after it increase it.
        versionCode = 20001
        versionName = "0.2.0-beta.1"
    }

    // Release signing (D72): read from apps/android/keystore.properties, which is git-ignored and is
    // never committed. Without it the release APK is built unsigned. Keys: storeFile, storePassword,
    // keyAlias, keyPassword. See docs/release/release-process.md.
    val signingProperties = rootProject.file("keystore.properties").takeIf { it.isFile }?.let { file ->
        Properties().apply { file.inputStream().use(::load) }
    }
    signingConfigs {
        if (signingProperties != null) {
            create("release") {
                storeFile = rootProject.file(signingProperties.getProperty("storeFile"))
                storePassword = signingProperties.getProperty("storePassword")
                keyAlias = signingProperties.getProperty("keyAlias")
                keyPassword = signingProperties.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            // Not minified: nothing has been run on a device with R8 applied, and the APK is small.
            isMinifyEnabled = false
            if (signingProperties != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }
}

dependencies {
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
    // android.jar only stubs org.json for unit tests. This is the real thing, for tests only; on a
    // device the platform's own org.json is used and nothing is added to the app.
    testImplementation(libs.org.json)
    // Self-signed certificates for the Wi-Fi TLS tests, which run on the desktop JVM without AndroidKeyStore.
    testImplementation(libs.bouncycastle.bcpkix)
}
