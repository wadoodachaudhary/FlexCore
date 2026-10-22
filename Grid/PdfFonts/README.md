# Embedded native PDF fonts

The twelve TrueType files are unchanged Liberation Sans, Serif and Mono fonts from
the environment's `fonts-liberation` package. `manifest.json` records their exact
byte counts and SHA-256 hashes. `OFL.txt` contains their SIL Open Font License
and copyright notices; no font is modified or renamed.

Native report PDFs embed only the styles they use. Arial/Helvetica aliases use
Liberation Sans, Times New Roman/Times aliases use Liberation Serif, and Courier
New/Courier aliases use Liberation Mono. These are metric-compatible substitutes,
not the proprietary Microsoft fonts. Each of regular, bold, italic and bold italic
has its own font. Fonts and metrics are read from assembly resources, never from
the host filesystem or a network endpoint.

The current character interface supports standalone Latin, Greek and Cyrillic BMP
characters and ordinary symbols present in the font. Unsupported families, missing
glyphs, surrogate halves and control characters produce explicit errors rather than
silently printing replacement question marks. Bidirectional format controls,
combining marks and script ranges requiring shaping are explicitly refused even
when the embedded font contains their glyphs. Precomposed accented Latin and Greek
characters remain supported. The font mapper does not perform bidirectional layout,
complex-script shaping, mark positioning or font fallback.
