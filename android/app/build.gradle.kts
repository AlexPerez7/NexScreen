import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "dev.nexscreen.client"
    compileSdk = 35

    defaultConfig {
        applicationId = "dev.nexscreen.client"
        minSdk = 26
        targetSdk = 35
        // El workflow de release pasa la versión del tag (-PversionName=0.6.0); versionCode se deriva
        // de ella para que cada versión nueva sea mayor y el host pueda actualizar la app.
        versionName = (project.findProperty("versionName") as String?) ?: "0.6.0"
        versionCode = versionCodeOf(versionName!!)
    }

    signingConfigs {
        // Clave de firma de los releases. Nunca va en el repo: viene de variables de entorno
        // (secretos en GitHub Actions; en el PC, ver installer\build-release.ps1).
        val keystore = System.getenv("NEXSCREEN_KEYSTORE")
        if (keystore != null) {
            create("release") {
                storeFile = file(keystore)
                storePassword = System.getenv("NEXSCREEN_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("NEXSCREEN_KEY_ALIAS")
                keyPassword = System.getenv("NEXSCREEN_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        viewBinding = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

/** "1.2.3" → 10203. Ordena las versiones como enteros (lo exige Android para actualizar). */
fun versionCodeOf(version: String): Int {
    val parts = version.substringBefore('-').split('.').map { it.toIntOrNull() ?: 0 } + listOf(0, 0, 0)
    return parts[0] * 10000 + parts[1] * 100 + parts[2]
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")

    testImplementation("junit:junit:4.13.2")
}
