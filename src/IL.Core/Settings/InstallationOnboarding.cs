namespace IL.Core.Settings;

public static class InstallationOnboarding
{
    public const string LicenseVersion = "2026-09-22";

    public static AppSettings RecommendedSettings(AppSettings initial) => initial with
    {
        EulaAcceptedVersion = LicenseVersion,
        FileAssociationEnabled = true,
        FileAssociationPrompted = true,
        CloudServiceEnabled = false,
        McpEnabled = false,
        TelemetryEnabled = true,
        TelemetryPrompted = true,
        SkipOpeningPrompts = true,
        CreateProjectOnAudioImport = true,
    };

    public static void Prepare(string root, bool hasPersonalSettings)
    {
        if (hasPersonalSettings) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "installation-onboarding.txt"), "full");
    }
    public static bool WelcomeOnly(string root) =>
        !File.Exists(Path.Combine(root, "installation-onboarding.txt")) ||
        File.ReadAllText(Path.Combine(root, "installation-onboarding.txt")).Trim() != "full";
}
