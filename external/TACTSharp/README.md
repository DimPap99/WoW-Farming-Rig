# TACTSharp source snapshot

Source: [wowdev/TACTSharp](https://github.com/wowdev/TACTSharp), commit
`a507ff7b485f7afce45e0a01dd8a15cfb2818728`. The upstream source is
MIT licensed; the full license is in [LICENSE](LICENSE).

The project file here is reduced to a library build without packaging metadata.
Local changes open archive files with read/write/delete sharing so the reader
works while the client is running; local-only mode avoids CDN discovery when a
file is absent. The memory-mapped reader classes and `BuildInstance` also have
`Dispose` methods so a CASC source releases mapped handles after use. Keep
these changes when updating the snapshot. No community listfile is included.
