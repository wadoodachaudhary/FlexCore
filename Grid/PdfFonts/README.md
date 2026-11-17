# Embedded native PDF fonts

The twelve Liberation TrueType files are unchanged Sans, Serif and Mono fonts from
the environment's `fonts-liberation` package. `manifest.json` records their exact
byte counts and SHA-256 hashes. `OFL.txt` contains their SIL Open Font License
and copyright notices; no font is modified or renamed.

Native report PDFs embed only the styles they use. Arial/Helvetica aliases use
Liberation Sans, Times New Roman/Times aliases use Liberation Serif, and Courier
New/Courier aliases use Liberation Mono. These are metric-compatible substitutes,
not the proprietary Microsoft fonts. Each of regular, bold, italic and bold italic
has its own font.

Calibri (including the viewer's `Fx Report Calibri` alias) uses the four bundled
Carlito faces, the same metric-compatible OFL family used by the report viewer.
They were losslessly decompressed from `../../Reports/Fonts/Carlito-*.woff2`
with fontTools 4.65.0. No glyph, metric or font-name edits were made. See
`carlito-manifest.json`, `../../Reports/Fonts/SOURCE.txt` and
`../../Reports/Fonts/OFL.txt` for provenance, hashes and the Carlito license.
Carlito is not proprietary Calibri and does not promise identical rasterization.

Bundled faces are read from assembly resources. Tahoma is the one supported
installed-font exception: regular/bold TrueType files must already exist in the
OS font directories listed in `NativePdfFont.InstalledTahoma`. Their embedding
permissions are checked. No Tahoma files are redistributed, downloaded or silently
substituted; missing fonts, restricted embedding and synthetic italic requests
fail explicitly. Linux/cloud deployments must provision their own licensed fonts.

The current character interface supports standalone Latin, Greek and Cyrillic BMP
characters and ordinary symbols present in the font. Unsupported families, missing
glyphs, surrogate halves and control characters produce explicit errors rather than
silently printing replacement question marks. Bidirectional format controls,
combining marks and script ranges requiring shaping are explicitly refused even
when the embedded font contains their glyphs. Precomposed accented Latin and Greek
characters remain supported. The font mapper does not perform bidirectional layout,
complex-script shaping, mark positioning or font fallback.
