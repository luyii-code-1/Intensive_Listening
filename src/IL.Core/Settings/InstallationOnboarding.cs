namespace IL.Core.Settings;

public static class InstallationOnboarding
{
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
