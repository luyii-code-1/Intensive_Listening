using System.Globalization;
namespace IL.Core.Management;

public sealed record ManagedLesson(string Id, string Title, string PackageUuid, int PackageVersion, string Url, string Sha256, long Size, string Signature)
{
    public string SignedContent => string.Join('\n', "ilp", Id, PackageUuid, PackageVersion.ToString(CultureInfo.InvariantCulture), Url, Sha256, Size.ToString(CultureInfo.InvariantCulture));
}
