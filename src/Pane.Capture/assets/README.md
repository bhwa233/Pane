# Pane Capture icons

`pane-capture-icons.ttf` is an outline-preserving subset of the `lucide.ttf`
distributed in `lucide-static@1.60.0`. It replaces ScreenCapture's original
icon-font resource; the original font is not distributed. The font retains its
`lucide` family name and code points. `../icons.json` records the chosen icons
and maps the upstream toolbar glyphs to them.

To regenerate it, extract `package/font/lucide.ttf` and
`package/font/codepoints.json` from the `lucide-static@1.60.0` npm archive.
Its SHA-512 SRI is
`sha512-Y8/IYjBYST40TuqzknGt1XJOcZp83UH5fLndgXlQCQ4lM97eZO3EQo6cIyDcBrprredIGDVxge8yslRyM4wD0Q==`.
From the repository root, with `$font` pointing to the extracted font:

```powershell
$icons = Get-Content src/Pane.Capture/icons.json -Raw | ConvertFrom-Json -AsHashtable
$unicode = ($icons.Values.codepoint | Sort-Object -Unique | ForEach-Object { "U+$_" }) -join ','
uvx --from fonttools==4.68.0 pyftsubset $font "--unicodes=$unicode" `
    --name-IDs='*' --name-languages='*' --no-recalc-timestamp `
    --output-file=src/Pane.Capture/assets/pane-capture-icons.ttf
```

The build uses the checked-in subset; it does not need npm, Python or FontTools.
