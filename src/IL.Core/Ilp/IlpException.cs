namespace IL.Core.Ilp;

public enum IlpError
{
    CorruptArchive, MissingFile, DuplicateEntry, InvalidManifest, UnsupportedVersion,
    InvalidPath, SymbolicLink, HashMismatch, InvalidTranscript, DuplicatePackage
}

public sealed class IlpException(IlpError code, string message, Exception? inner = null) : Exception(message, inner)
{
    public IlpError Code { get; } = code;
}

public sealed record SrtCue(TimeSpan Start, TimeSpan End, string Text);

public sealed record ImportedLesson(string Id, string DirectoryPath, IlpManifest Manifest, IReadOnlyList<SrtCue> Cues)
{
    public string AudioPath => Path.Combine(DirectoryPath, Manifest.AudioPath);
    public string TranscriptPath => Path.Combine(DirectoryPath, Manifest.TranscriptPath);
}
