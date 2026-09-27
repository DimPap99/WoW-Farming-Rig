using System;
using System.IO;

namespace PPather.Storage;

/// <summary>
/// Opens logical client files independently of their on-disk container.
/// Paths use WoW's backslash-separated names; returned streams are readable
/// and seekable because the ADT/WMO/M2 parsers seek within each file.
/// </summary>
public interface IGameFileSource : IDisposable
{
    Stream OpenRead(ReadOnlySpan<char> name);
    bool Exists(ReadOnlySpan<char> name);

    // MPQ sources have no FileDataID namespace; CASC sources override these.
    bool SupportsFileDataIds => false;
    Stream OpenRead(uint fileDataId) =>
        throw new NotSupportedException("This game-file source cannot open FileDataIDs.");
    bool Exists(uint fileDataId) => false;
}
