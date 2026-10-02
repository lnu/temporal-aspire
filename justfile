[windows]
set shell := ["pwsh.exe", "-NoLogo", "-Command"]

solution      := "TemporalAspire.slnx"
configuration := "Release"
artifacts_dir := "artifacts/packages"
tests_dir     := "tests/TemporalCommunity.Aspire.Hosting.Tests"
package_dir   := "src/TemporalCommunity.Aspire.Hosting"
package_id    := "TemporalCommunity.Aspire.Hosting"
# Runs minver (local tool — .config/dotnet-tools.json) to compute the current version from git tags.
# The sed/tr reads MinVerDefaultPreReleaseIdentifiers from Directory.Build.props so the pre-release
# label has a single source of truth; minver-cli must be installed via `dotnet tool restore`.
prerelease    := `sed -n 's/.*<MinVerDefaultPreReleaseIdentifiers>\(.*\)<\/MinVerDefaultPreReleaseIdentifiers>.*/\1/p' Directory.Build.props | tr -d ' '`
version       := `dotnet tool run minver --tag-prefix "" --default-pre-release-identifiers "$(just --evaluate prerelease)" --verbosity error`
commit        := `git rev-parse HEAD`

default:
    @just --list

info:
    @echo "Solution:      {{solution}}"
    @echo "Package:       {{package_id}}"
    @echo "Version:       {{version}}"
    @echo "Prerelease:    {{prerelease}}"
    @echo "Configuration: {{configuration}}"
    @echo "Artifacts:     {{artifacts_dir}}"

restore:
    dotnet restore "{{solution}}"

build: restore
    dotnet build "{{solution}}" --configuration "{{configuration}}" --no-restore --nologo

test: build
    dotnet test "{{tests_dir}}" --configuration "{{configuration}}" --no-build --nologo --logger "trx;LogFileName=unit.trx"

integration-test: build
    RUN_TEMPORAL_INTEGRATION_TESTS=1 dotnet test "{{tests_dir}}" --configuration "{{configuration}}" --no-build --nologo --filter "FullyQualifiedName~TemporalDevServerIntegrationTests"

pack: build
    mkdir -p "{{artifacts_dir}}"
    dotnet pack "{{package_dir}}" --configuration "{{configuration}}" --no-build --nologo --output "{{artifacts_dir}}"

pack-verify: pack
    dotnet run "eng/VerifyPackage.cs" -- "{{artifacts_dir}}/{{package_id}}.{{version}}.nupkg" "{{artifacts_dir}}/{{package_id}}.{{version}}.snupkg" "{{commit}}"

publish-nuget: pack
    dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --source "https://api.nuget.org/v3/index.json" \
        --api-key "$NUGET_API_KEY" \
        --skip-duplicate
