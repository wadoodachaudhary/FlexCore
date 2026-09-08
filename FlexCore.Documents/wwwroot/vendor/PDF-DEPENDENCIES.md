# Bundled PDF dependencies

- PDF.js / pdfjs-dist 6.3.289 (Mozilla and contributors), Apache-2.0. Source: https://github.com/mozilla/pdf.js. Built browser distributions, worker, character maps, standard fonts and WASM decoders from the npm release are unmodified. Each asset directory retains its upstream licenses.
- PDF-Lib 1.17.1 (Andrew Dillon and contributors), MIT. Source: https://github.com/Hopding/pdf-lib. The unmodified minified ESM distribution is renamed from `.js` to `.mjs` for explicit module loading. License is in `pdf-lib/LICENSE.md`.

The viewer imports assets from the FlexCore static web asset path. It does not use a CDN or send PDF contents to a remote processor. Update these pinned releases together with the PDF browser regression checks.
