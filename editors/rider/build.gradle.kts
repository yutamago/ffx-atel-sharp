// SPDX-License-Identifier: MIT
//
// The Rider plugin (JetBrains' Rider plugin template, IntelliJ Platform Gradle Plugin 2.x): the backend in C#
// (src/dotnet, built and tested with `dotnet`), and the frontend part Rider needs (plugin.xml and the settings
// page, src/rider). `./gradlew buildPlugin` writes build/distributions/atel-text-<version>.zip; `./gradlew check`
// also runs the backend tests (headless). Never published.

plugins {
    id("java")
    alias(libs.plugins.kotlinJvm)
    alias(libs.plugins.intellijPlatform)
}

val pluginVersion = providers.gradleProperty("PluginVersion").get()
val productVersion = providers.gradleProperty("ProductVersion").get()
val buildConfiguration = providers.gradleProperty("BuildConfiguration").get()

version = pluginVersion

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

kotlin {
    jvmToolchain(21)
}

sourceSets {
    main {
        kotlin.srcDir("src/rider/main/kotlin")
        resources.srcDir("src/rider/main/resources")
    }
}

dependencies {
    intellijPlatform {
        rider(productVersion) {
            useInstaller = false
        }
        // SimpleOptionsPage (the frontend side of the backend's settings page).
        bundledModule("intellij.rider.rdclient.dotnet")
    }
}

intellijPlatform {
    pluginConfiguration {
        version = pluginVersion
        ideaVersion {
            // The backend is built against this Rider's SDK; ReSharper's API changes between releases.
            sinceBuild = "263"
            untilBuild = "263.*"
        }
    }
    // Indexing the settings page for search starts the IDE (headless, but heavy); the page is found by its name.
    buildSearchableOptions = false
    instrumentCode = false
}

val dotnetPlugin = "src/dotnet/Ffx.Atel.Rider/Ffx.Atel.Rider.csproj"
val dotnetTests = "src/dotnet/Ffx.Atel.Rider.Tests/Ffx.Atel.Rider.Tests.csproj"
val dotnetOutput = "src/dotnet/Ffx.Atel.Rider/bin/$buildConfiguration"

val compileDotNet = tasks.register<Exec>("compileDotNet") {
    group = "build"
    description = "Builds the backend (dotnet build)."
    workingDir = projectDir
    commandLine("dotnet", "build", dotnetPlugin, "-c", buildConfiguration, "-nologo", "-v", "q")
    inputs.dir("src/dotnet/Ffx.Atel.Rider").withPropertyName("sources")
    inputs.files("src/dotnet/Directory.Build.props", "src/dotnet/Plugin.props")
    outputs.file("$dotnetOutput/Ffx.Atel.Rider.dll")
}

val testDotNet = tasks.register<Exec>("testDotNet") {
    group = "verification"
    description = "Runs the backend tests (ReSharper test framework, headless)."
    workingDir = projectDir
    commandLine("dotnet", "test", dotnetTests, "-nologo")
}

tasks.check {
    dependsOn(testDotNet)
}

tasks.prepareSandbox {
    dependsOn(compileDotNet)
    from(listOf("$dotnetOutput/Ffx.Atel.Rider.dll", "$dotnetOutput/Ffx.Atel.Rider.pdb")) {
        into("${rootProject.name}/dotnet")
    }
}
