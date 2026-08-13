/* FlexKit/FlexCore — browser-side helpers for EditorControl.
 *
 * Loaded by consumer apps via
 *   <script src="_content/{FlexKit-or-FlexCore}/editor-control.js"></script>
 *
 * Exposes a single namespace `fxEditor` with the primitives EditorControl's
 * C# side calls. All helpers take the editor's DOM id as the first argument
 * so multiple EditorControls can coexist on one page.
 *
 * Inline-HTML allowlist: <b>, <i>, <u>, <s>, <strong>, <em>, plus
 *   <span style="color|font-family|font-size|font-weight">. Block-level
 * styling preserved on readback via the data-align attribute and
 * style="line-height:…" on the block element.
 */

(function () {
    "use strict";

    var INLINE_ALLOWED = { B: "b", I: "i", U: "u", S: "s", STRONG: "strong", EM: "em" };

    function safeStyleValue(v) {
        if (!v) return "";
        v = String(v).trim();
        if (!v) return "";
        if (/[<>"\\]/.test(v)) return "";
        return v;
    }

    function inlineStyleFrom(node) {
        var parts = [];
        if (node.style) {
            var color = safeStyleValue(node.style.color);
            if (color) parts.push("color:" + color);
            var fontFamily = safeStyleValue(node.style.fontFamily);
            if (fontFamily) parts.push("font-family:" + fontFamily);
            var fontSize = safeStyleValue(node.style.fontSize);
            if (fontSize) parts.push("font-size:" + fontSize);
            var fontWeight = safeStyleValue(node.style.fontWeight);
            if (fontWeight) parts.push("font-weight:" + fontWeight);
        }
        if (!parts.length && node.tagName === "FONT") {
            var attrColor = safeStyleValue(node.getAttribute("color"));
            if (attrColor) parts.push("color:" + attrColor);
        }
        return parts.join(";");
    }

    function serializeBlockContent(blockEl) {
        // Escape an attribute value for safe re-emission. The DOM parser
        // already gave us a decoded string; we just need to keep the
        // double-quote / angle-bracket boundaries intact so the resulting
        // markup parses back the same way.
        function attrEscape(v) {
            if (v == null) return "";
            return String(v)
                .replace(/&/g, "&amp;")
                .replace(/</g, "&lt;")
                .replace(/>/g, "&gt;")
                .replace(/"/g, "&quot;");
        }
        function walk(node) {
            if (!node) return "";
            if (node.nodeType === 3) return node.nodeValue || "";
            if (node.nodeType !== 1) return "";
            var inner = "";
            for (var c = 0; c < node.childNodes.length; c++) {
                inner += walk(node.childNodes[c]);
            }
            var tag = node.tagName ? node.tagName.toUpperCase() : "";
            if (INLINE_ALLOWED[tag]) {
                if (!inner) return "";
                return "<" + INLINE_ALLOWED[tag] + ">" + inner + "</" + INLINE_ALLOWED[tag] + ">";
            }
            if (tag === "FONT" || tag === "SPAN") {
                // Preserve GhostWriter "gw-inserted" wrappers verbatim
                // (class + data-* + title) so the chip metadata + remove
                // affordance survive DOM round-trips. Without this branch
                // every read-back of the editor strips the wrap and the
                // bordered chip + × button disappear after the first
                // SyncEditorToModelAsync.
                if (tag === "SPAN" && node.classList && node.classList.contains("gw-inserted")) {
                    var attrs = ' class="' + attrEscape(node.getAttribute("class") || "") + '"';
                    if (node.hasAttribute("title")) {
                        attrs += ' title="' + attrEscape(node.getAttribute("title")) + '"';
                    }
                    if (node.attributes) {
                        for (var ai = 0; ai < node.attributes.length; ai++) {
                            var a = node.attributes[ai];
                            if (a.name && a.name.indexOf("data-") === 0) {
                                attrs += ' ' + a.name + '="' + attrEscape(a.value) + '"';
                            }
                        }
                    }
                    return "<span" + attrs + ">" + inner + "</span>";
                }
                if (!inner) return "";
                var style = inlineStyleFrom(node);
                if (style) return '<span style="' + style + '">' + inner + "</span>";
                return inner;
            }
            // GhostWriter per-insertion remove (×) button — preserved
            // verbatim so it round-trips through the editor's read.
            // Other <button> elements aren't expected inside the editor;
            // they fall through to the inner-only path below.
            if (tag === "BUTTON" && node.classList && node.classList.contains("gw-inserted-remove")) {
                return '<button class="gw-inserted-remove" type="button" contenteditable="false" title="Remove this insertion">×</button>';
            }
            return inner;
        }
        var out = "";
        for (var i = 0; i < blockEl.childNodes.length; i++) {
            out += walk(blockEl.childNodes[i]);
        }
        return out;
    }

    function escapeHtml(text) {
        var div = document.createElement("div");
        div.appendChild(document.createTextNode(text || ""));
        return div.innerHTML;
    }

    function escapeAttr(value) {
        return String(value || "")
            .replace(/&/g, "&amp;")
            .replace(/"/g, "&quot;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    function resolveContainer(editor) {
        // Positioning reference for floating-toolbar coordinates. The editor's
        // nearest positioned ancestor is the implicit coordinate space.
        var el = editor.parentElement;
        while (el) {
            var pos = window.getComputedStyle(el).position;
            if (pos && pos !== "static") return el;
            el = el.parentElement;
        }
        return editor;
    }

    function findScrollableAncestor(el) {
        var node = el;
        while (node) {
            if (node.nodeType === 1) {
                var style = window.getComputedStyle(node);
                var overflowY = style ? style.overflowY : "";
                if ((overflowY === "auto" || overflowY === "scroll") &&
                    node.scrollHeight > node.clientHeight + 1) {
                    return node;
                }
            }
            node = node.parentElement;
        }
        return document.scrollingElement || document.documentElement || document.body;
    }

    // ─── Block-HTML rendering ────────────────────────────────────────────
    // Pulled out of init() so the paginator can re-render a single block
    // (e.g. a split-paragraph chunk) with the same markup contract.
    function renderBlockHtml(b) {
        var content = (typeof b.html === "string" && b.html.length > 0)
            ? b.html
            : escapeHtml(b.text || "");
        // Empty text blocks need a <br> placeholder so the browser renders
        // them with full line-height. Without it, <p></p> collapses to zero
        // height — pressing Enter on its own to add a blank line, followed
        // by the auto-repaginate, would visually erase the new line and the
        // text below would snap back up. Image blocks don't need this since
        // the <img> already provides intrinsic height.
        if (!content && b.kind !== "Image") {
            content = "<br>";
        }
        var alignAttr = (typeof b.alignment === "string" && b.alignment && b.alignment !== "left")
            ? ' data-align="' + b.alignment + '"'
            : '';
        var styleAttr = "";
        if (typeof b.lineHeight === "string" && b.lineHeight && !/[<>"\\]/.test(b.lineHeight)) {
            styleAttr = ' style="line-height: ' + b.lineHeight + '"';
        }
        if (b.kind === "ChapterHeading") {
            return '<h2 class="doc-heading fx-doc-heading" data-block-id="' + b.id + '"' + alignAttr + styleAttr + ">" + content + "</h2>";
        }
        if (b.kind === "SectionHeading") {
            return '<h3 class="doc-subheading fx-doc-subheading" data-block-id="' + b.id + '"' + alignAttr + styleAttr + ">" + content + "</h3>";
        }
        if (b.kind === "Image") {
            var src = escapeAttr(b.imageSrc || "");
            var alt = escapeAttr(b.text || "");
            return '<figure class="doc-image fx-doc-image" data-block-id="' + b.id + '" data-image-src="' + src + '"' + alignAttr + styleAttr + ' contenteditable="false">' +
                   '<img src="' + src + '" alt="' + alt + '" draggable="false" />' +
                   "</figure>";
        }
        if (b.kind === "PageBreak") {
            // Non-editable visual marker; the paginator reads its kind off
            // the data attribute and forces a new page after it.
            return '<div class="doc-page-break fx-doc-page-break" data-block-id="' + b.id +
                   '" data-block-kind="PageBreak" contenteditable="false" aria-label="Page break">' +
                       '<span class="fx-doc-page-break-line"></span>' +
                       '<span class="fx-doc-page-break-label">Page break</span>' +
                       '<span class="fx-doc-page-break-line"></span>' +
                   '</div>';
        }
        return '<p class="doc-paragraph fx-doc-paragraph" data-block-id="' + b.id + '"' + alignAttr + styleAttr + ">" + content + "</p>";
    }

    // Block IDs created on the fly when the browser hands us a paragraph
    // without one (typical after a contenteditable Enter). Stable for the
    // lifetime of the JS process — once a block has an ID we keep using it
    // so caret-restore + read() merging both line up.
    var _hfFreshIdCounter = 0;
    function freshBlockId() {
        _hfFreshIdCounter++;
        return "blk-new-" + Date.now().toString(36) + "-" + _hfFreshIdCounter;
    }

    // ─── Pagination ──────────────────────────────────────────────────────
    // Real measurement-based page packing. Treats the chapter as a stream of
    // words, not paragraphs: short paragraphs are packed onto the same page,
    // and a paragraph that's too long for one page is split at the nearest
    // word boundary across consecutive pages so no content is ever lost or
    // clipped.
    //
    // The algorithm:
    //   1. Probe a real .fx-page card (already laid out via the CSS Grid) to
    //      learn the content area's width × height in pixels.
    //   2. Build an off-screen measurement div with the same width and font
    //      properties as a page's content area.
    //   3. Greedy-pack blocks: try adding each block to the current page;
    //      keep it when its rendered height stays under the page budget.
    //   4. When a block doesn't fit:
    //        a. If the current page is empty, the block is bigger than a
    //           full page → call splitParagraphByHeight to chop it at word
    //           boundaries so each chunk fits on its own page.
    //        b. Otherwise close the current page and try the same block on
    //           a fresh one.
    //   5. Split-paragraph chunks all carry the original block-id; the read()
    //      pass uses data-split-cont="true" markers to merge them back into
    //      one paragraph when the host syncs.
    //
    // Caveats:
    //   - Mid-paragraph splits drop inline formatting (bold/italic/spans) on
    //     the chunked text — a bold paragraph that spans two pages becomes
    //     plain text in the spilled portion. Acceptable trade-off for v1;
    //     fixing this needs a node-walking splitter.
    //   - Editing inside a paged card doesn't auto-repaginate. The host
    //     re-pushes when needed (chapter switch, format change, explicit
    //     "Repaginate" button).
    function paginateByMeasurement(editor, blocks, blockHtml) {
        // Off-screen .fx-page placed *inside* the editor so paragraphs in it
        // inherit the same scoped CSS rules the visible cards use (paragraph
        // margins, font, line-height, padding). A probe outside the editor
        // would measure with the browser's default p { margin: 1em 0 } and
        // dramatically over-count each paragraph's height.
        var probe = document.createElement("div");
        probe.className = "fx-page";
        probe.style.cssText =
            "position: absolute !important; left: -100000px !important; top: 0 !important; " +
            "visibility: hidden !important; pointer-events: none !important; " +
            "height: auto !important;";
        editor.appendChild(probe);

        var pageH = readLengthVar(editor, "--fx-page-h", 768);

        function fits(html) {
            probe.innerHTML = html;
            return probe.scrollHeight <= pageH;
        }

        var pages = [[]];
        var currentHtml = "";

        for (var i = 0; i < blocks.length; i++) {
            var b  = blocks[i];
            var bh = blockHtml[i];

            // PageBreak: always close the current page and consume the marker
            // on it (so the user can see *where* the break is when scrolling
            // back), then start the next block on a fresh page. The marker
            // itself is short and effectively zero-height for fitting math,
            // so this never overflows.
            if (b.kind === "PageBreak") {
                pages[pages.length - 1].push(bh);
                pages.push([]);
                currentHtml = "";
                continue;
            }

            // Whole block fits on the current page — easy case.
            if (fits(currentHtml + bh)) {
                pages[pages.length - 1].push(bh);
                currentHtml += bh;
                continue;
            }

            // Block doesn't fit. For non-splittable kinds (headings, images)
            // start a fresh page; if the block is also bigger than a full
            // empty page, accept the overflow (the card will clip at the
            // bottom edge — better than dropping content).
            if (b.kind !== "Paragraph") {
                if (pages[pages.length - 1].length > 0) {
                    pages.push([]);
                    currentHtml = "";
                }
                pages[pages.length - 1].push(bh);
                currentHtml += bh;
                continue;
            }

            // Splittable paragraph — pack words greedily across pages so the
            // bottom of every page is filled. Each iteration finds the
            // largest word-aligned prefix that fits on the *current* page
            // (which may already have content above), emits it, then either
            // exits (whole paragraph emitted) or opens a new empty page and
            // continues with the remainder.
            var info = parseParagraphInfo(b, bh);
            if (info.words.length === 0) continue;

            var startWord = 0;
            var isContinuation = false;
            while (startWord < info.words.length) {
                var pageHasContent = currentHtml.length > 0;
                var fitEnd = largestFittingWordEnd(info, startWord, currentHtml, isContinuation, fits);

                if (fitEnd <= startWord) {
                    // Nothing fits on the current page.
                    if (pageHasContent) {
                        // Close the page and retry on a fresh empty one.
                        pages.push([]);
                        currentHtml = "";
                        continue;
                    }
                    // Empty page can't fit even one word — accept overflow
                    // (pathological font / dimension combo) and emit one
                    // word so we don't infinite-loop.
                    fitEnd = Math.min(startWord + 1, info.words.length);
                }

                var chunkHtml = info.makeChunk(
                    info.words.slice(startWord, fitEnd).join(" "), isContinuation);
                pages[pages.length - 1].push(chunkHtml);
                currentHtml += chunkHtml;
                startWord = fitEnd;
                isContinuation = true;

                // More words left → this chunk filled (or nearly filled) the
                // page. Open a fresh one for the next chunk.
                if (startWord < info.words.length) {
                    pages.push([]);
                    currentHtml = "";
                }
            }
        }

        editor.removeChild(probe);
        return pages;
    }

    // Binary-searches the word list for the largest prefix length such that
    // (currentHtml + chunk(words[startWord..end])) still fits on the page.
    // Returns startWord when nothing fits.
    function largestFittingWordEnd(info, startWord, currentHtml, isContinuation, fits) {
        var lo = startWord + 1, hi = info.words.length, best = startWord;
        while (lo <= hi) {
            var mid = (lo + hi) >> 1;
            var chunk = info.makeChunk(info.words.slice(startWord, mid).join(" "), isContinuation);
            if (fits(currentHtml + chunk)) {
                best = mid;
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }
        return best;
    }

    // Extracts the open-tag attributes (block-id, alignment, line-height) from
    // a rendered <p> block and pre-builds a makeChunk function that re-emits
    // a slice of words with those same attributes. Continuation chunks carry
    // data-split-cont="true" so read() folds them back into the parent block.
    // textContent is used (not innerHTML) — inline formatting on split chunks
    // is dropped in v1 to keep the splitter simple.
    function parseParagraphInfo(b, html) {
        var tmp = document.createElement("div");
        tmp.innerHTML = html;
        var pEl = tmp.firstElementChild;
        if (!pEl) return { words: [], makeChunk: function () { return ""; } };

        var text = (pEl.textContent || pEl.innerText || "").replace(/\s+/g, " ").trim();
        var words = text ? text.split(" ") : [];
        var blockId = (pEl.dataset && pEl.dataset.blockId) ? pEl.dataset.blockId : (b.id || "");
        var alignAttr = (pEl.dataset && pEl.dataset.align)
            ? ' data-align="' + pEl.dataset.align + '"'
            : '';
        var lhStyle = (pEl.style && pEl.style.lineHeight) ? pEl.style.lineHeight : "";
        var styleAttr = lhStyle ? ' style="line-height: ' + lhStyle + '"' : "";

        return {
            words: words,
            makeChunk: function (sliceText, isContinuation) {
                var contAttr = isContinuation ? ' data-split-cont="true"' : '';
                return '<p class="doc-paragraph fx-doc-paragraph" data-block-id="' +
                       blockId + '"' + alignAttr + styleAttr + contAttr + '>' +
                       escapeHtml(sliceText) + '</p>';
            }
        };
    }

    // Reads a CSS custom property as a length in pixels, with unit conversion
    // for in/cm/mm/pt/pc. Falls back to the supplied default when the variable
    // is unset or unparseable.
    function readLengthVar(el, name, fallback) {
        var raw = window.getComputedStyle(el).getPropertyValue(name);
        if (!raw) return fallback;
        return parseCssLength(raw.trim(), fallback);
    }
    function parseCssLength(str, fallback) {
        if (!str) return fallback;
        var m = str.match(/^(-?[\d.]+)(px|in|cm|mm|pt|pc|em|rem)?$/);
        if (!m) return fallback;
        var n = parseFloat(m[1]);
        if (!isFinite(n)) return fallback;
        switch ((m[2] || "px").toLowerCase()) {
            case "px": return n;
            case "in": return n * 96;
            case "cm": return n * (96 / 2.54);
            case "mm": return n * (96 / 25.4);
            case "pt": return n * (96 / 72);
            case "pc": return n * 16;
            default:   return n;     // em / rem — treat as px for our purposes
        }
    }

    // ─── Trailing blank page + "+ New page" affordance ──────────────────
    // Always present in paged mode. The blank page is a real .fx-page (so
    // typing into it produces normal block markup); the add-button is
    // contenteditable="false" so its click doesn't insert into the editor
    // and doesn't get serialised as content on save.
    function buildTrailingBlankPageHtml(nextPageIdx) {
        return '<div class="fx-page fx-page-blank" data-page-index="' + nextPageIdx + '" data-page-blank="true"></div>' +
               '<div class="fx-page-add" data-fx-action="add-blank-page" contenteditable="false" role="button" tabindex="0" title="Add another blank page after this one">' +
                   '<span class="fx-page-add-plus">+</span>' +
                   '<span class="fx-page-add-label">New page</span>' +
               '</div>';
    }

    // Wires the focus / click / selection listeners that drive paged-mode UX:
    //   • Track which .fx-page the caret is in. When it moves to a different
    //     page AND the editor is dirty since the last pagination, schedule a
    //     debounced re-pagination so paragraph splits / blank-bottom space
    //     get fixed up. No-op when the user is just reading (clean editor).
    //   • Click on the "+ New page" card → insert another blank .fx-page just
    //     before the trailing one and place the caret inside it.
    // Bound once per editor element via the _hfPagedHandlersBound flag so
    // repeated init() calls don't stack listeners.
    function bindPagedHandlers(editor) {
        if (editor._hfPagedHandlersBound) return;
        editor._hfPagedHandlersBound = true;

        editor.addEventListener("click", function (e) {
            var btn = (e.target && e.target.closest)
                ? e.target.closest('[data-fx-action="add-blank-page"]')
                : null;
            if (!btn) return;
            e.preventDefault();
            e.stopPropagation();
            insertNewBlankPage(editor, btn);
        });

        // selectionchange is a document-level event; we filter to selections
        // that land inside this editor. Debounced so rapid arrow-key moves
        // within one page don't all fire repaginate checks.
        document.addEventListener("selectionchange", function () {
            if (!editor.classList.contains("fx-paged")) return;
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0) return;
            var range = sel.getRangeAt(0);
            if (!editor.contains(range.commonAncestorContainer)) return;
            var node = range.commonAncestorContainer;
            var pageEl = null;
            while (node && node !== editor) {
                if (node.nodeType === 1 && node.classList && node.classList.contains("fx-page")) {
                    pageEl = node;
                    break;
                }
                node = node.parentNode;
            }
            if (!pageEl) return;
            var pageIdx = parseInt(pageEl.getAttribute("data-page-index") || "-1", 10);
            var lastPage = editor._hfLastFocusedPage;
            editor._hfLastFocusedPage = pageIdx;

            // Cursor moved across page boundaries AND there are unflushed
            // edits → re-paginate. Skip when clean (just navigation).
            if (lastPage === undefined || lastPage === pageIdx) return;
            if (!editor._hfDirtySincePagination) return;

            clearTimeout(editor._hfRepagTimer);
            editor._hfRepagTimer = setTimeout(function () {
                fxEditor.repaginate(editor.id);
            }, 250);
        });
    }

    // Schedules a re-paginate during typing/deleting using a TRAILING-EDGE
    // THROTTLE (not a debounce). The earlier debounce-style implementation
    // reset its timer on every keystroke, which meant continuous deletion
    // — holding Backspace, key-repeating at ~50 ms intervals — never let
    // the timer expire, so the bottom of the page kept growing whitespace
    // until the user finally let go. A throttle fires at fixed cadence
    // regardless of activity, so the page reflows continuously while keys
    // are still being pressed.
    //
    // Pattern: the first input event after an idle period schedules a
    // repaginate 80 ms in the future. Subsequent inputs inside that
    // window are no-ops (the flag dedupes them). When the timer fires,
    // it runs repaginate and clears the flag, opening the door for the
    // next round. Worst-case cadence: ~80 ms = ~12 reflows / second,
    // which feels continuous to the eye while bounding the per-second
    // pagination cost on long chapters.
    //
    // captureCaret / restoreCaret inside repaginate() preserve the
    // (blockId, charOffset) so the caret keeps tracking the typing
    // position across each reflow.
    function scheduleOverflowRepaginate(editor) {
        if (editor._hfRepagPending) return;
        editor._hfRepagPending = true;
        setTimeout(function () {
            editor._hfRepagPending = false;
            if (editor.classList.contains("fx-paged")) {
                fxEditor.repaginate(editor.id);
            }
        }, 80);
    }

    function insertNewBlankPage(editor, anchorBtn) {
        var newPage = document.createElement("div");
        newPage.className = "fx-page fx-page-blank";
        newPage.setAttribute("data-page-blank", "true");
        // Insert right before the "+ New page" button so the trailing blank
        // page (the existing scratchpad) and the new blank page sit together
        // ahead of the affordance.
        editor.insertBefore(newPage, anchorBtn);
        // Drop the caret inside the new page so the user can start typing.
        try {
            newPage.focus();
            var range = document.createRange();
            range.selectNodeContents(newPage);
            range.collapse(true);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
        } catch (_) { /* best-effort caret placement */ }
    }

    // Captures the caret's block-id + offset *within the merged paragraph*.
    // Why "merged": when a paragraph is split across pages by the paginator,
    // the DOM has multiple <p data-block-id="X"> siblings (chunk 1 with no
    // marker + chunks 2..N tagged data-split-cont). read() folds them into
    // one block with text = chunk1 + " " + chunk2 + " " + … so the offset
    // we save here must use the same coordinate space — otherwise, after a
    // re-pagination splits the paragraph differently, restoreCaret would
    // land the cursor in the wrong chunk (or at a wrong position within
    // the right chunk).
    function captureCaret(editor) {
        var sel = window.getSelection();
        if (!sel || sel.rangeCount === 0) return null;
        var range = sel.getRangeAt(0);
        if (!editor.contains(range.commonAncestorContainer)) return null;
        var node = range.commonAncestorContainer;
        var blockEl = null;
        while (node && node !== editor) {
            if (node.nodeType === 1 && node.dataset && node.dataset.blockId) {
                blockEl = node;
                break;
            }
            node = node.parentNode;
        }
        if (!blockEl) return null;

        var blockId = blockEl.dataset.blockId;
        // Walk every chunk that shares this block-id in document order.
        // Sum the text length of chunks BEFORE the caret's chunk, then add
        // the offset within the caret's chunk. The +1 between non-empty
        // chunks mirrors the joiner space read() inserts when it merges.
        var chunks = editor.querySelectorAll('[data-block-id="' + blockId + '"]');
        var mergedOffset = 0;
        for (var i = 0; i < chunks.length; i++) {
            var chunk = chunks[i];
            if (chunk === blockEl) {
                var pre = range.cloneRange();
                pre.selectNodeContents(chunk);
                pre.setEnd(range.startContainer, range.startOffset);
                mergedOffset += pre.toString().length;
                return { blockId: blockId, offset: mergedOffset };
            }
            var chunkLen = (chunk.textContent || "").length;
            mergedOffset += chunkLen + (chunkLen > 0 ? 1 : 0);
        }
        // Fallback (shouldn't happen): caret isn't inside any matching chunk.
        return { blockId: blockId, offset: mergedOffset };
    }

    function restoreCaret(editor, caret) {
        if (!caret || !caret.blockId) return;
        var chunks = editor.querySelectorAll('[data-block-id="' + caret.blockId + '"]');
        if (chunks.length === 0) return;

        // Walk chunks in document order, decrementing the remaining offset by
        // each chunk's text length (+1 per joiner) until we find the chunk
        // that contains the caret position.
        var remaining = caret.offset;
        var targetChunk = null;
        for (var i = 0; i < chunks.length; i++) {
            var chunk = chunks[i];
            var chunkLen = (chunk.textContent || "").length;
            if (remaining <= chunkLen) {
                targetChunk = chunk;
                break;
            }
            // Move past this chunk: full length + joiner space (only when
            // non-empty, mirroring read()'s merge).
            remaining -= chunkLen + (chunkLen > 0 ? 1 : 0);
            if (remaining < 0) {
                // We landed on the joiner — clamp to start of next chunk.
                targetChunk = chunks[i + 1] || chunk;
                remaining = 0;
                break;
            }
        }
        if (!targetChunk) {
            targetChunk = chunks[chunks.length - 1];
            remaining = (targetChunk.textContent || "").length;
        }

        // Now walk text nodes inside the target chunk to find the exact text
        // node + nodeOffset for the remaining offset.
        var range = document.createRange();
        var walker = document.createTreeWalker(targetChunk, NodeFilter.SHOW_TEXT, null);
        var consumed = 0;
        var node, placed = false;
        while ((node = walker.nextNode())) {
            var len = node.nodeValue ? node.nodeValue.length : 0;
            if (consumed + len >= remaining) {
                range.setStart(node, remaining - consumed);
                range.collapse(true);
                placed = true;
                break;
            }
            consumed += len;
        }
        if (!placed) {
            range.selectNodeContents(targetChunk);
            range.collapse(false);   // end of chunk
        }
        var sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
    }

    var fxEditor = {

        /** Replaces editor contents with a block array produced by EditorControl.
         *  pageOpts (optional): { paged, pagedCssClass }
         *    - paged: when true, blocks are laid out as a CSS Grid of fixed-
         *      size page cards. Each card holds as many blocks as fit, with
         *      mid-paragraph splits at word boundaries when a single block
         *      would overflow on its own. Page dimensions come from CSS
         *      custom properties (--fx-page-w, --fx-page-h, --fx-page-pad)
         *      on the editor; the host sets these via pagedCssClass.
         *    - pagedCssClass: extra class added to the editor root (e.g.
         *      "paged-paperback") — host CSS uses it to set per-format page
         *      width/height via the variables above.
         *  When paged is false (or pageOpts omitted), blocks are emitted as
         *  flat siblings directly inside the editor (continuous Web view).
         */
        init: function (editorId, blocks, pageOpts) {
            var editor = document.getElementById(editorId);
            if (!editor) return;

            // Build the per-block markup once.
            var blockHtml = new Array(blocks.length);
            for (var i = 0; i < blocks.length; i++) {
                blockHtml[i] = renderBlockHtml(blocks[i]);
            }

            // Strip any prior paged-* class so switching modes doesn't leave
            // stale layout active.
            editor.classList.remove("fx-paged");
            for (var prev = editor.classList.length - 1; prev >= 0; prev--) {
                var cls = editor.classList[prev];
                if (cls && cls.indexOf("paged-") === 0) editor.classList.remove(cls);
            }

            var paged = !!(pageOpts && pageOpts.paged);
            if (paged) {
                editor.classList.add("fx-paged");
                if (pageOpts.pagedCssClass) editor.classList.add(pageOpts.pagedCssClass);

                // First a flat innerHTML so getComputedStyle resolves layout
                // dimensions correctly (CSS variables on .fx-page must be
                // evaluated against the editor's live styles).
                editor.innerHTML = blockHtml.join("");

                var pages = paginateByMeasurement(editor, blocks, blockHtml);
                var html = "";
                var contentPageCount = 0;
                for (var p = 0; p < pages.length; p++) {
                    if (!pages[p] || !pages[p].length) continue;
                    html += '<div class="fx-page" data-page-index="' + p + '">';
                    html += pages[p].join("");
                    html += '</div>';
                    contentPageCount++;
                }
                // ALWAYS append a trailing blank page so the user has a place
                // to type new content past the current end-of-chapter, plus
                // a "+ New page" affordance card next to it. Both are part of
                // the editor's contenteditable surface; the button is wrapped
                // in contenteditable=false so its click fires normally and
                // it doesn't get serialized as document content.
                html += buildTrailingBlankPageHtml(contentPageCount);
                editor.innerHTML = html;
            } else {
                editor.innerHTML = blockHtml.join("");
            }

            editor.dataset.dirty = "false";
            if (!editor._hfDirtyBound) {
                editor._hfDirtyBound = true;
                editor.addEventListener("input", function () {
                    editor.dataset.dirty = "true";
                    editor._hfDirtySincePagination = true;
                    // In paged mode, every keystroke is a candidate for
                    // re-flow: if the caret's page has overflowed (typed
                    // past the bottom of the card), the over-spill needs
                    // to flow into the next page rather than render off the
                    // bottom edge. Debounced so a fast typist doesn't pay
                    // the full pagination cost on every key.
                    if (editor.classList.contains("fx-paged")) {
                        scheduleOverflowRepaginate(editor);
                    }
                });
            }
            bindPagedHandlers(editor);
        },

        /**
         * Re-runs measurement-based pagination on the editor's current DOM
         * content. Used when the user has typed enough into one page that it
         * overflows or leaves a too-empty bottom; called automatically on
         * cursor moves between pages (see bindPagedHandlers) and exposed
         * here so hosts can also trigger it explicitly (e.g. a "Repaginate"
         * toolbar button or after a programmatic Apply).
         *
         * Process: capture caret → re-read DOM blocks (read() merges split
         * paragraph chunks back into single blocks) → re-init via the same
         * paged code path (which lays out fresh page cards + appends the
         * trailing blank page + button) → restore caret to the same
         * (block-id, offset) so the user's editing position is preserved.
         */
        repaginate: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor || !editor.classList.contains("fx-paged")) return;

            var caret = captureCaret(editor);

            // Find the current per-format class so re-init keeps the same
            // page width / height / typography.
            var pagedCssClass = "";
            for (var c = 0; c < editor.classList.length; c++) {
                var name = editor.classList[c];
                if (name && name.indexOf("paged-") === 0) {
                    pagedCssClass = name;
                    break;
                }
            }

            // Read current blocks (split-cont chunks merged into their parent
            // paragraphs so the paginator gets paragraph-shaped input).
            var raw = fxEditor.read(editorId);
            var dtos = raw.map(function (b) {
                return {
                    id:         b.id || "",
                    kind:       b.kind || "Paragraph",
                    text:       b.text || "",
                    // text from read() preserves sanitized inline tags
                    // (<b>, <i>, <span style="…">), so passing it as html
                    // keeps that formatting through the re-paginate.
                    html:       b.text || "",
                    alignment:  b.alignment || "",
                    lineHeight: b.lineHeight || "",
                    imageSrc:   b.imageSrc || ""
                };
            });

            fxEditor.init(editorId, dtos,
                { paged: true, pagedCssClass: pagedCssClass });

            restoreCaret(editor, caret);
            editor._hfDirtySincePagination = false;
        },

        isDirty: function (editorId) {
            var editor = document.getElementById(editorId);
            return !!(editor && editor.dataset.dirty === "true");
        },

        read: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor) return [];
            var results = [];

            // Build the iteration source: in flat mode every child of the
            // editor IS a block; in paged mode the editor's children are
            // .fx-page wrappers and the actual blocks live one level deeper.
            // querySelectorAll on the wrappers' selector walks both layouts
            // in document order, so block sequence is preserved either way.
            var blockEls;
            if (editor.classList.contains("fx-paged")) {
                blockEls = editor.querySelectorAll(
                    ":scope > .fx-page > h2," +
                    ":scope > .fx-page > h3," +
                    ":scope > .fx-page > p," +
                    ":scope > .fx-page > figure," +
                    ":scope > .fx-page > .doc-page-break");
            } else {
                blockEls = editor.children;
            }

            for (var i = 0; i < blockEls.length; i++) {
                var el = blockEls[i];
                // Browsers don't tag the <p> they create on Enter — stamp a
                // fresh data-block-id so captureCaret can anchor the cursor
                // to this paragraph after the next re-paginate. Existing IDs
                // (from C#-side renders or prior reads) are preserved.
                if (el.dataset && !el.dataset.blockId) {
                    el.dataset.blockId = freshBlockId();
                }
                var tag = el.tagName.toUpperCase();
                if (tag === "FIGURE") {
                    var img = el.querySelector("img");
                    var srcAttr = el.dataset ? (el.dataset.imageSrc || "") : "";
                    // Fallback to the <img> element when the data attribute
                    // wasn't set (e.g. legacy DOM from an older push).
                    if (!srcAttr && img) srcAttr = img.getAttribute("src") || "";
                    results.push({
                        id: el.dataset ? (el.dataset.blockId || "") : "",
                        kind: "Image",
                        text: img ? (img.getAttribute("alt") || "") : "",
                        alignment: el.dataset ? (el.dataset.align || "") : "",
                        lineHeight: el.style ? (el.style.lineHeight || "") : "",
                        imageSrc: srcAttr
                    });
                    continue;
                }
                // Page-break marker: round-trips as a PageBreak block with no
                // text / image. The visible "Page break" label inside the div
                // is decorative and excluded from text serialization.
                if (el.classList && el.classList.contains("doc-page-break")) {
                    results.push({
                        id: el.dataset ? (el.dataset.blockId || "") : "",
                        kind: "PageBreak",
                        text: "",
                        alignment: "",
                        lineHeight: "",
                        imageSrc: ""
                    });
                    continue;
                }
                var kind = tag === "H2" ? "ChapterHeading"
                    : tag === "H3" ? "SectionHeading"
                    : "Paragraph";
                var isCont = !!(el.dataset && el.dataset.splitCont === "true");
                var blockText = serializeBlockContent(el);

                // Continuation chunk of a paginator-split paragraph → fold
                // its text back into the previous block instead of emitting
                // a new one. This is how the host model stays paragraph-
                // shaped even though the DOM was sliced across pages.
                if (isCont && results.length > 0) {
                    var prev = results[results.length - 1];
                    var prevId = el.dataset ? (el.dataset.blockId || "") : "";
                    if (prev.kind === "Paragraph" && prev.id === prevId) {
                        var joiner = (prev.text && blockText) ? " " : "";
                        prev.text = (prev.text || "") + joiner + (blockText || "");
                        continue;
                    }
                }

                results.push({
                    id: el.dataset ? (el.dataset.blockId || "") : "",
                    kind: kind,
                    text: blockText,
                    alignment: el.dataset ? (el.dataset.align || "") : "",
                    lineHeight: el.style ? (el.style.lineHeight || "") : "",
                    imageSrc: ""
                });
            }
            return results;
        },

        getSelection: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor) return null;
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return null;
            var range = sel.getRangeAt(0);
            if (!editor.contains(range.commonAncestorContainer)) return null;
            var text = sel.toString().trim();
            if (!text) return null;
            var container = resolveContainer(editor);
            var rect = range.getBoundingClientRect();
            var containerRect = container.getBoundingClientRect();
            var node = range.commonAncestorContainer;
            var blockId = "";
            while (node && node !== editor) {
                if (node.nodeType === 1 && node.dataset && node.dataset.blockId) {
                    blockId = node.dataset.blockId;
                    break;
                }
                node = node.parentNode;
            }
            return {
                text: text,
                blockId: blockId,
                top: rect.top - containerRect.top + container.scrollTop,
                left: rect.left - containerRect.left,
                width: rect.width,
                bottom: rect.bottom - containerRect.top + container.scrollTop
            };
        },

        selectAll: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            var range = document.createRange();
            range.selectNodeContents(editor);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
        },

        clearSelection: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            var sel = window.getSelection();
            if (sel) sel.removeAllRanges();
            try { editor.focus({ preventScroll: true }); } catch (e) { /* best-effort */ }
        },

        execCommand: function (editorId, command, value) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            if (!editor.contains(document.activeElement)) editor.focus();
            try {
                document.execCommand(command, false, value || null);
                editor.dataset.dirty = "true";
            } catch (e) { /* best-effort */ }
        },

        /**
         * Wraps the selection in a <span style="property: value">. Splits
         * text nodes at the selection boundaries and updates existing
         * single-child spans in place to avoid runaway nesting.
         */
        applyInlineStyle: function (editorId, property, value) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            if (!editor.contains(document.activeElement)) editor.focus();
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return;
            var range = sel.getRangeAt(0);
            if (!editor.contains(range.commonAncestorContainer)) return;

            if (range.startContainer.nodeType === 3 &&
                range.startOffset > 0 &&
                range.startOffset < (range.startContainer.nodeValue || "").length) {
                var splitStart = range.startContainer.splitText(range.startOffset);
                range.setStart(splitStart, 0);
            }
            if (range.endContainer.nodeType === 3 &&
                range.endOffset > 0 &&
                range.endOffset < (range.endContainer.nodeValue || "").length) {
                range.endContainer.splitText(range.endOffset);
            }

            var walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT, null);
            var nodes = [];
            var cur = walker.nextNode();
            while (cur) {
                if (range.intersectsNode(cur) && cur.nodeValue && cur.nodeValue.length > 0) {
                    nodes.push(cur);
                }
                cur = walker.nextNode();
            }

            var wrapped = [];
            for (var i = 0; i < nodes.length; i++) {
                var tn = nodes[i];
                var parent = tn.parentNode;
                if (parent && parent.nodeType === 1 &&
                    parent.tagName === "SPAN" &&
                    parent.childNodes.length === 1 &&
                    parent !== editor) {
                    parent.style.setProperty(property, value);
                    wrapped.push(parent);
                } else {
                    var span = document.createElement("span");
                    span.style.setProperty(property, value);
                    parent.insertBefore(span, tn);
                    span.appendChild(tn);
                    wrapped.push(span);
                }
            }

            if (wrapped.length > 0) {
                var nr = document.createRange();
                nr.setStartBefore(wrapped[0].firstChild || wrapped[0]);
                nr.setEndAfter(wrapped[wrapped.length - 1].lastChild || wrapped[wrapped.length - 1]);
                sel.removeAllRanges();
                sel.addRange(nr);
            }
            editor.dataset.dirty = "true";
        },

        /** Sets a block-level style (line-height) on intersecting blocks. */
        applyBlockStyle: function (editorId, property, value) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0) return;
            var range = sel.getRangeAt(0);
            var matched = 0;
            for (var i = 0; i < editor.children.length; i++) {
                var el = editor.children[i];
                if (!el.dataset) continue;
                if (range.intersectsNode(el)) {
                    if (!value) el.style.removeProperty(property);
                    else el.style.setProperty(property, value);
                    matched++;
                }
            }
            if (matched === 0 && document.activeElement && editor.contains(document.activeElement)) {
                var n = document.activeElement;
                while (n && n.parentNode !== editor) n = n.parentNode;
                if (n && n.dataset) {
                    if (!value) n.style.removeProperty(property);
                    else n.style.setProperty(property, value);
                }
            }
            editor.dataset.dirty = "true";
        },

        setBlockAlignment: function (editorId, align) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0) return;
            var range = sel.getRangeAt(0);
            for (var i = 0; i < editor.children.length; i++) {
                var el = editor.children[i];
                if (!el.dataset) continue;
                if (range.intersectsNode(el)) {
                    if (align && align !== "left") el.setAttribute("data-align", align);
                    else el.removeAttribute("data-align");
                }
            }
            editor.dataset.dirty = "true";
        },

        getCaret: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor) return { blockId: "", offset: 0 };
            var sel = window.getSelection();
            if (!sel || sel.rangeCount === 0) return { blockId: "", offset: 0 };
            var range = sel.getRangeAt(0);
            if (!editor.contains(range.startContainer)) return { blockId: "", offset: 0 };
            var node = range.startContainer;
            var blockEl = null;
            while (node && node !== editor) {
                if (node.nodeType === 1 && node.dataset && node.dataset.blockId) {
                    blockEl = node;
                    break;
                }
                node = node.parentNode;
            }
            if (!blockEl) return { blockId: "", offset: 0 };

            // Return offset in MERGED-paragraph coordinates so setCaret /
            // restoreCaret can place the cursor consistently after a
            // re-paginate that may have re-split the same paragraph at
            // different word boundaries. We walk every <p data-block-id="X">
            // sibling in document order (chunk 1 + data-split-cont chunks),
            // sum the text length of chunks BEFORE the caret's chunk, then
            // add the caret's offset within its chunk. The +1 between
            // non-empty chunks mirrors the joiner space read() inserts when
            // it merges chunks back into a single block.
            var blockId = blockEl.dataset.blockId;
            var chunks = editor.querySelectorAll('[data-block-id="' + blockId + '"]');
            var mergedOffset = 0;
            for (var i = 0; i < chunks.length; i++) {
                var chunk = chunks[i];
                if (chunk === blockEl) {
                    var pre = range.cloneRange();
                    pre.selectNodeContents(chunk);
                    pre.setEnd(range.startContainer, range.startOffset);
                    mergedOffset += pre.toString().length;
                    return { blockId: blockId, offset: mergedOffset };
                }
                var chunkLen = (chunk.textContent || "").length;
                mergedOffset += chunkLen + (chunkLen > 0 ? 1 : 0);
            }
            return { blockId: blockId, offset: mergedOffset };
        },

        setCaret: function (blockId, offset) {
            if (!blockId) return;

            // A paragraph that the paginator split across pages has multiple
            // <p data-block-id="X"> siblings (chunk 1 + chunks tagged with
            // data-split-cont). The saved offset is in the *merged* paragraph's
            // coordinate space (matching how captureCaret / read() count it),
            // so we walk all matching elements in document order and decrement
            // offset by each chunk's length + 1 for the joiner space — the
            // same accounting restoreCaret in repaginate() uses. Without this,
            // setCaret would always land in the FIRST chunk and clamp to its
            // end, which is why an Undo / Redo on a split paragraph took the
            // cursor to the wrong place.
            var chunks = document.querySelectorAll('[data-block-id="' + blockId + '"]');
            if (chunks.length === 0) return;

            var remaining = Math.max(0, offset || 0);
            var targetEl = null;
            for (var i = 0; i < chunks.length; i++) {
                var chunk = chunks[i];
                var chunkLen = (chunk.textContent || "").length;
                if (remaining <= chunkLen) {
                    targetEl = chunk;
                    break;
                }
                // Move past this chunk: full text + joiner space (only when
                // non-empty, mirroring read()'s merge logic).
                remaining -= chunkLen + (chunkLen > 0 ? 1 : 0);
                if (remaining < 0) {
                    targetEl = chunks[i + 1] || chunk;
                    remaining = 0;
                    break;
                }
            }
            if (!targetEl) {
                targetEl = chunks[chunks.length - 1];
                remaining = (targetEl.textContent || "").length;
            }

            var sel = window.getSelection();
            var range = document.createRange();
            var walker = document.createTreeWalker(targetEl, NodeFilter.SHOW_TEXT, null);
            var node = walker.nextNode();
            while (node) {
                var len = node.nodeValue ? node.nodeValue.length : 0;
                if (remaining <= len) {
                    range.setStart(node, remaining);
                    range.collapse(true);
                    sel.removeAllRanges();
                    sel.addRange(range);
                    // Bring the caret's block into view. Without this, an
                    // Undo / Redo / Apply call that lands on a block that
                    // happens to be scrolled off-screen makes the user
                    // think the cursor "went to the top" — really the
                    // caret was placed correctly but the viewport never
                    // followed. block: "center" keeps the target near the
                    // middle of the visible area instead of pinning it to
                    // an edge where typing would scroll right back away.
                    targetEl.focus({ preventScroll: true });
                    targetEl.scrollIntoView({ behavior: "auto", block: "center" });
                    return;
                }
                remaining -= len;
                node = walker.nextNode();
            }
            range.selectNodeContents(targetEl);
            range.collapse(false);
            sel.removeAllRanges();
            sel.addRange(range);
            targetEl.scrollIntoView({ behavior: "auto", block: "center" });
            el.focus({ preventScroll: true });
        },

        scrollToBlock: function (blockId) {
            if (!blockId) return;
            var el = document.querySelector('[data-block-id="' + blockId + '"]');
            if (el) el.scrollIntoView({ behavior: "smooth", block: "start" });
        },

        scrollToTop: function (editorId) {
            var editor = document.getElementById(editorId);
            if (!editor) return;
            var scroller = findScrollableAncestor(editor);
            if (scroller && typeof scroller.scrollTo === "function") {
                scroller.scrollTo({ top: 0, behavior: "smooth" });
            } else if (scroller) {
                scroller.scrollTop = 0;
            }
        }
    };

    window.fxEditor = fxEditor;
    window.hfEditor = fxEditor;

    // ── EditorLiteControl helpers ────────────────────────────────────────
    var fxEditorLite = (function () {
        var allowedTags = {
            P: true, DIV: true, BR: true, B: true, STRONG: true, I: true, EM: true,
            U: true, S: true, STRIKE: true, UL: true, OL: true, LI: true,
            H1: true, H2: true, H3: true, BLOCKQUOTE: true, CODE: true, PRE: true,
            A: true, SPAN: true
        };

        function liteEscapeHtml(text) {
            return String(text || "")
                .replace(/&/g, "&amp;")
                .replace(/</g, "&lt;")
                .replace(/>/g, "&gt;");
        }

        function safeUrl(url) {
            url = String(url || "").trim();
            if (!url) return "";
            if (/^(https?:|mailto:|#|\/)/i.test(url)) return url;
            return "";
        }

        function safeInlineStyle(node) {
            var parts = [];
            if (!node || !node.style) return "";
            ["color", "backgroundColor", "fontWeight", "fontStyle", "textDecorationLine"].forEach(function (prop) {
                var value = node.style[prop];
                if (!value || /[<>"\\]/.test(value)) return;
                var css = prop.replace(/[A-Z]/g, function (m) { return "-" + m.toLowerCase(); });
                parts.push(css + ":" + value);
            });
            return parts.join(";");
        }

        function sanitizeHtml(html) {
            if (!html) return "";
            var template = document.createElement("template");
            template.innerHTML = String(html);

            function walk(node) {
                if (node.nodeType === 3) return document.createTextNode(node.nodeValue || "");
                if (node.nodeType !== 1) return document.createTextNode("");

                var tag = node.tagName.toUpperCase();
                if (!allowedTags[tag]) {
                    var frag = document.createDocumentFragment();
                    Array.prototype.forEach.call(node.childNodes, function (child) {
                        frag.appendChild(walk(child));
                    });
                    return frag;
                }

                var outTag = tag === "DIV" ? "p" : tag.toLowerCase();
                var el = document.createElement(outTag);
                if (tag === "A") {
                    var href = safeUrl(node.getAttribute("href"));
                    if (href) {
                        el.setAttribute("href", href);
                        el.setAttribute("rel", "noopener noreferrer");
                    }
                }
                if (tag === "SPAN") {
                    var style = safeInlineStyle(node);
                    if (style) el.setAttribute("style", style);
                }
                Array.prototype.forEach.call(node.childNodes, function (child) {
                    el.appendChild(walk(child));
                });
                return el;
            }

            var clean = document.createElement("div");
            Array.prototype.forEach.call(template.content.childNodes, function (child) {
                clean.appendChild(walk(child));
            });
            return clean.innerHTML;
        }

        function editor(editorId) {
            return document.getElementById(editorId);
        }

        function normalizeInitialHtml(html) {
            html = sanitizeHtml(html || "");
            if (!html.trim()) return "";
            if (!/<(p|h1|h2|h3|ul|ol|blockquote|pre)\b/i.test(html)) {
                return "<p>" + html + "</p>";
            }
            return html;
        }

        function installPasteSanitizer(el) {
            if (!el || el.__fxEditorLitePaste) return;
            el.__fxEditorLitePaste = true;
            el.addEventListener("paste", function (event) {
                if (!event.clipboardData) return;
                // HHM-145: if the clipboard holds an image (e.g. a screenshot), embed it as a data-URL <img>
                // instead of dropping it. Checked before the text/html path so a copied image isn't lost.
                var items = event.clipboardData.items || [];
                for (var i = 0; i < items.length; i++) {
                    if (items[i] && items[i].type && items[i].type.indexOf("image/") === 0) {
                        var file = items[i].getAsFile();
                        if (file) {
                            event.preventDefault();
                            var reader = new FileReader();
                            reader.onload = function (ev) {
                                document.execCommand("insertHTML", false,
                                    '<img src="' + ev.target.result + '" style="max-width:100%;height:auto;" alt="pasted image" />');
                            };
                            reader.readAsDataURL(file);
                            return;
                        }
                    }
                }
                event.preventDefault();
                var html = event.clipboardData.getData("text/html");
                var text = event.clipboardData.getData("text/plain");
                var payload = html ? sanitizeHtml(html) : liteEscapeHtml(text).replace(/\r?\n/g, "<br>");
                document.execCommand("insertHTML", false, payload);
            });
        }

        function plainTextFrom(node) {
            return (node ? node.innerText : "") || "";
        }

        function inlineMarkdown(node) {
            if (!node) return "";
            if (node.nodeType === 3) return node.nodeValue || "";
            if (node.nodeType !== 1) return "";
            var text = "";
            Array.prototype.forEach.call(node.childNodes, function (child) {
                text += inlineMarkdown(child);
            });
            var tag = node.tagName.toUpperCase();
            if (!text) return "";
            if (tag === "STRONG" || tag === "B") return "**" + text + "**";
            if (tag === "EM" || tag === "I") return "*" + text + "*";
            if (tag === "U") return "<u>" + text + "</u>";
            if (tag === "S" || tag === "STRIKE") return "~~" + text + "~~";
            if (tag === "CODE") return "`" + text + "`";
            if (tag === "A") {
                var href = safeUrl(node.getAttribute("href"));
                return href ? "[" + text + "](" + href + ")" : text;
            }
            return text;
        }

        function blockMarkdown(node, index) {
            if (!node || node.nodeType !== 1) return "";
            var tag = node.tagName.toUpperCase();
            if (tag === "UL" || tag === "OL") {
                var lines = [];
                Array.prototype.forEach.call(node.children, function (li, i) {
                    if (li.tagName && li.tagName.toUpperCase() === "LI") {
                        lines.push((tag === "OL" ? (i + 1) + ". " : "- ") + inlineMarkdown(li).trim());
                    }
                });
                return lines.join("\n");
            }
            if (tag === "H1") return "# " + inlineMarkdown(node).trim();
            if (tag === "H2") return "## " + inlineMarkdown(node).trim();
            if (tag === "H3") return "### " + inlineMarkdown(node).trim();
            if (tag === "BLOCKQUOTE") return "> " + inlineMarkdown(node).trim();
            if (tag === "PRE") return "```\n" + plainTextFrom(node).trim() + "\n```";
            return inlineMarkdown(node).trim();
        }

        function toMarkdown(el) {
            if (!el) return "";
            var blocks = Array.prototype.filter.call(el.childNodes, function (n) {
                return n.nodeType === 1 || (n.nodeType === 3 && String(n.nodeValue || "").trim());
            });
            if (!blocks.length) return plainTextFrom(el).trim();
            return blocks.map(blockMarkdown).filter(Boolean).join("\n\n").trim();
        }

        function marksForNode(node) {
            var marks = [];
            var current = node && node.parentElement;
            while (current) {
                var tag = current.tagName ? current.tagName.toUpperCase() : "";
                if (tag === "B" || tag === "STRONG") marks.push({ type: "strong" });
                if (tag === "I" || tag === "EM") marks.push({ type: "em" });
                if (tag === "U") marks.push({ type: "underline" });
                if (tag === "S" || tag === "STRIKE") marks.push({ type: "strike" });
                if (tag === "CODE") marks.push({ type: "code" });
                if (tag === "A") {
                    var href = safeUrl(current.getAttribute("href"));
                    if (href) marks.push({ type: "link", attrs: { href: href } });
                }
                current = current.parentElement;
                if (current && current.classList && current.classList.contains("fx-editor-lite-surface")) break;
            }
            return marks;
        }

        function adfInlineContent(node) {
            var content = [];
            var walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
            var textNode;
            while ((textNode = walker.nextNode())) {
                var text = textNode.nodeValue || "";
                if (!text) continue;
                var item = { type: "text", text: text };
                var marks = marksForNode(textNode);
                if (marks.length) item.marks = marks;
                content.push(item);
            }
            return content;
        }

        function paragraphAdf(node) {
            var content = adfInlineContent(node);
            return content.length ? { type: "paragraph", content: content } : { type: "paragraph" };
        }

        function blockAdf(node) {
            var tag = node.tagName ? node.tagName.toUpperCase() : "";
            if (tag === "H1" || tag === "H2" || tag === "H3") {
                return { type: "heading", attrs: { level: tag === "H1" ? 1 : tag === "H2" ? 2 : 3 }, content: adfInlineContent(node) };
            }
            if (tag === "BLOCKQUOTE") {
                return { type: "blockquote", content: [paragraphAdf(node)] };
            }
            if (tag === "UL" || tag === "OL") {
                return {
                    type: tag === "UL" ? "bulletList" : "orderedList",
                    content: Array.prototype.filter.call(node.children, function (li) {
                        return li.tagName && li.tagName.toUpperCase() === "LI";
                    }).map(function (li) {
                        return { type: "listItem", content: [paragraphAdf(li)] };
                    })
                };
            }
            if (tag === "PRE") {
                return { type: "codeBlock", content: [{ type: "text", text: plainTextFrom(node) }] };
            }
            return paragraphAdf(node);
        }

        function toAdfJson(el) {
            var content = [];
            Array.prototype.forEach.call(el ? el.childNodes : [], function (node) {
                if (node.nodeType === 1) content.push(blockAdf(node));
                else if (node.nodeType === 3 && String(node.nodeValue || "").trim()) {
                    content.push({ type: "paragraph", content: [{ type: "text", text: node.nodeValue }] });
                }
            });
            return JSON.stringify({ type: "doc", version: 1, content: content });
        }

        function rtfEscape(text) {
            return String(text || "")
                .replace(/\\/g, "\\\\")
                .replace(/{/g, "\\{")
                .replace(/}/g, "\\}")
                .replace(/\n/g, "\\par ");
        }

        function inlineRtf(node) {
            if (!node) return "";
            if (node.nodeType === 3) return rtfEscape(node.nodeValue || "");
            if (node.nodeType !== 1) return "";
            var inner = "";
            Array.prototype.forEach.call(node.childNodes, function (child) { inner += inlineRtf(child); });
            var tag = node.tagName.toUpperCase();
            if (tag === "B" || tag === "STRONG") return "\\b " + inner + "\\b0 ";
            if (tag === "I" || tag === "EM") return "\\i " + inner + "\\i0 ";
            if (tag === "U") return "\\ul " + inner + "\\ul0 ";
            if (tag === "S" || tag === "STRIKE") return "\\strike " + inner + "\\strike0 ";
            return inner;
        }

        function toRtf(el) {
            var body = "";
            Array.prototype.forEach.call(el ? el.childNodes : [], function (node) {
                if (node.nodeType !== 1) return;
                var tag = node.tagName.toUpperCase();
                if (tag === "UL" || tag === "OL") {
                    Array.prototype.forEach.call(node.children, function (li, i) {
                        body += (tag === "OL" ? (i + 1) + ". " : "\\bullet ") + inlineRtf(li) + "\\par ";
                    });
                } else {
                    body += inlineRtf(node) + "\\par ";
                }
            });
            return "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}\\fs22 " + body + "}";
        }

        function toOpenDocumentHtml(html) {
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>EditorLite Document</title>" +
                "<style>body{font-family:Arial,sans-serif;font-size:11pt;line-height:1.5;} p{margin:0 0 9pt;} h2,h3{margin:0 0 9pt;font-weight:bold;}</style>" +
                "</head><body>" + html + "</body></html>";
        }

        return {
            setHtml: function (editorId, html) {
                var el = editor(editorId);
                if (!el) return;
                installPasteSanitizer(el);
                el.innerHTML = normalizeInitialHtml(html);
            },
            getHtml: function (editorId) {
                var el = editor(editorId);
                return sanitizeHtml(el ? el.innerHTML : "");
            },
            focus: function (editorId) {
                var el = editor(editorId);
                if (el) el.focus();
            },
            exec: function (editorId, command) {
                var el = editor(editorId);
                if (!el) return;
                el.focus();
                document.execCommand(command, false, null);
            },
            formatBlock: function (editorId, blockName) {
                var el = editor(editorId);
                if (!el) return;
                el.focus();
                document.execCommand("formatBlock", false, blockName || "P");
            },
            createLink: function (editorId) {
                var el = editor(editorId);
                if (!el) return;
                el.focus();
                var url = window.prompt("Link URL");
                url = safeUrl(url);
                if (url) document.execCommand("createLink", false, url);
            },
            read: function (editorId) {
                var el = editor(editorId);
                var html = sanitizeHtml(el ? el.innerHTML : "");
                var shell = document.createElement("div");
                shell.innerHTML = html;
                var text = plainTextFrom(shell).trim();
                var markdown = toMarkdown(shell);
                return {
                    schema: "fx-editor-lite/1",
                    html: html,
                    text: text,
                    markdown: markdown,
                    jiraText: markdown || text,
                    jiraDocumentJson: toAdfJson(shell),
                    rtf: toRtf(shell),
                    openDocumentHtml: toOpenDocumentHtml(html)
                };
            }
        };
    })();

    window.fxEditorLite = fxEditorLite;
    window.hfEditorLite = fxEditorLite;

    // ── Clipboard helpers — plain-text copy from result cards ──
    //
    // The result card renders rich HTML (with fonts, background tints, tab
    // chrome, etc.). Selecting text inside it and using the browser's own
    // Copy would carry those styles into the clipboard as `text/html`; when
    // the user then pastes into the contenteditable editor, the styles
    // override the document's formatting. Two fixes:
    //
    //   (1) installCopyInterceptor — a single capture-phase `copy` listener
    //       on document that detects when the selection is inside a
    //       `.fx-result-card` and rewrites the clipboard payload to plain
    //       text only (both text/plain AND text/html, the latter also plain
    //       so hosts that prefer text/html still land on unstyled content).
    //
    //   (2) copyFromCard — invoked by the card's Copy button. Copies the
    //       current selection if it's inside the card; else falls back to
    //       the full output body's innerText. Always plain text.
    //
    // Idempotent: repeated calls to installCopyInterceptor do nothing past
    // the first one. Safe to call from every card's OnAfterRenderAsync.
    var fxClipboard = {
        _copyInterceptorInstalled: false,

        installCopyInterceptor: function () {
            if (this._copyInterceptorInstalled) return;
            this._copyInterceptorInstalled = true;
            document.addEventListener("copy", function (e) {
                var sel = window.getSelection();
                if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return;
                var range = sel.getRangeAt(0);
                var node = range.commonAncestorContainer;
                if (node && node.nodeType === 3) node = node.parentElement;
                if (!node || typeof node.closest !== "function") return;
                if (!node.closest(".fx-result-card")) return;

                // Intercept. Write selection's plain text only; mirror it as
                // text/html so clipboards that prefer HTML still get
                // unstyled content (no font, no background, no inline CSS).
                var plain = sel.toString();
                if (e.clipboardData) {
                    e.preventDefault();
                    e.clipboardData.setData("text/plain", plain);
                    e.clipboardData.setData("text/html", plain);
                }
            }, true);
        },

        /**
         * Plain-text clipboard writer for hosts that already have a string
         * in hand (e.g. the History overlay's Copy button, where the source
         * isn't a DOM selection but a stored LLM response). Same
         * async-first / execCommand-fallback pattern as copyFromCard.
         */
        writeText: async function (text) {
            var payload = text || "";
            try {
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    await navigator.clipboard.writeText(payload);
                    return true;
                }
            } catch (err) { /* fall through to execCommand fallback */ }

            var ta = document.createElement("textarea");
            ta.value = payload;
            ta.setAttribute("readonly", "");
            ta.style.position = "fixed";
            ta.style.top = "0";
            ta.style.left = "0";
            ta.style.opacity = "0";
            document.body.appendChild(ta);
            ta.focus();
            ta.select();
            var ok = false;
            try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
            document.body.removeChild(ta);
            return ok;
        },

        copyFromCard: async function (cardId) {
            var card = document.getElementById(cardId);
            if (!card) return false;

            var text = "";
            var sel = window.getSelection();
            if (sel && !sel.isCollapsed && sel.rangeCount > 0) {
                var range = sel.getRangeAt(0);
                if (card.contains(range.commonAncestorContainer)) {
                    text = sel.toString();
                }
            }
            if (!text) {
                // Prefer the visible output body; fall back to the whole
                // card if the body isn't there (e.g. pending / error slot).
                var body = card.querySelector(".fx-result-output") || card;
                text = body.innerText || "";
            }

            try {
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    await navigator.clipboard.writeText(text);
                    return true;
                }
            } catch (err) { /* fall through to execCommand fallback */ }

            // execCommand fallback — needed on non-secure contexts (HTTP on
            // a LAN IP) and in some older browsers.
            var ta = document.createElement("textarea");
            ta.value = text;
            ta.setAttribute("readonly", "");
            ta.style.position = "fixed";
            ta.style.top = "0";
            ta.style.left = "0";
            ta.style.opacity = "0";
            document.body.appendChild(ta);
            ta.focus();
            ta.select();
            var ok = false;
            try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
            document.body.removeChild(ta);
            return ok;
        }
    };

    window.fxClipboard = fxClipboard;
    window.hfClipboard = fxClipboard;
})();
