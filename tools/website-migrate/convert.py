#!/usr/bin/env python3
"""
Convert dmart.cc's hand-written Svelte pages into a dmart `website` space.

One-time migration, but written to be re-runnable and deterministic: the same
input produces byte-identical output, so a re-run after editing a source page is
a reviewable diff rather than a mystery.

WHY THIS IS VIABLE AT ALL
-------------------------
The 14 pages are 8,046 lines of markup, and none of it uses a Svelte component.
Each page is semantic HTML over a 33-class vocabulary, so the structure maps
onto markdown without inventing anything:

    <h2>/<h3>                   -> ##  / ###
    <p>                         -> paragraph
    <div class=code-container>  -> fenced code block
    <pre class=mermaid>         -> ```mermaid fence
    <div class=table-container> -> GFM table
    <ul>/<ol>                   -> list
    <strong>/<em>/<code>        -> ** / _ / `

WHAT IS LOST, STATED PLAINLY
----------------------------
Per-page visual treatment. `endpoint`/`method`/`plugin-card` and friends are
presentation, and a uniform template cannot reproduce them. The api-docs page
feels this most: 60 endpoint blocks become headings plus code. The *content*
survives intact; the styling does not. That is the cost of moving content into
a CMS, and it is the decision recorded in docs/catalog-ssg-plan.md.

Titles and URLs come from App.svelte's routes map rather than being re-derived,
because that map is what the live site actually uses.

Output is the dmart import/export layout (`Services/ImportExportService.cs:21`):

    website/.dm/meta.space.json
    website/pages/.dm/meta.folder.json
    website/pages.json
    website/pages/.dm/<shortname>/meta.content.json
    website/pages/<shortname>.md

Usage:
    python3 tools/website-migrate/convert.py --src ../website/src --out /tmp/site

THE SEED IS NOW THE SOURCE
--------------------------
seed/spaces/website has been edited directly since the migration (#345's
ENABLE_MCP notes, #357's WEBSITE_* settings and `website` command), and the
Svelte pages are being retired. Never copy a fresh run over the seed — that
silently reverts those edits. To land a converter fix, run the old and the
fixed converter and apply only their difference to each page:

    git merge-file seed/spaces/website/pages/X.md old/website/pages/X.md new/website/pages/X.md
"""
from __future__ import annotations

import argparse
import html
import json
import re
import sys
import uuid
from html.parser import HTMLParser
from pathlib import Path

# Deterministic UUIDs: a re-run must not churn every meta file. Namespace is
# arbitrary but fixed.
UUID_NS = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")

# Fixed timestamps. The alternative is `now`, which would make every re-run a
# diff and defeat the determinism the CI check relies on.
CREATED_AT = "2026-10-01T00:00:00"


def det_uuid(*parts: str) -> str:
    return str(uuid.uuid5(UUID_NS, "/".join(parts)))


# --------------------------------------------------------------- source parse
def read_routes(app_svelte: Path) -> list[tuple[str, str, str]]:
    """(url_path, dir_name, title) from App.svelte's routes map, in order."""
    s = app_svelte.read_text(encoding="utf-8")
    block = re.search(r"const routes[^=]*=\s*\{(.*?)\n  \};", s, re.S)
    if not block:
        raise SystemExit("could not find the routes map in App.svelte")
    out = []
    for m in re.finditer(
        r'"(/[^"]+)":\s*\{\s*load:\s*\(\)\s*=>\s*import\("\./([^/]+)/\+page\.svelte"\),\s*title:\s*"([^"]*)"',
        block.group(1),
    ):
        out.append((m.group(1), m.group(2), html.unescape(m.group(3))))
    return out


def strip_blocks(markup: str) -> str:
    """Remove <script> and <style>, leaving the template."""
    markup = re.sub(r"<script[^>]*>.*?</script>", "", markup, flags=re.S)
    markup = re.sub(r"<style[^>]*>.*?</style>", "", markup, flags=re.S)
    return markup


# ------------------------------------------------------------------ inline md
# Every closing tag below is matched as `</tag\s*>`, never `</tag>`.
#
# Prettier formats this source in whitespace-sensitive mode, which puts a tag's
# closing bracket on the next line to avoid introducing significant whitespace:
#
#     <tr><th>Setting</th>...</tr
#     >
#     <tr
#         ><td><code>APP_URL</code></td><td
#             >Public base URL...</td
#         >
#
# Patterns requiring an immediate `>` silently match nothing here. That is not a
# hypothetical: it dropped 13 of 14 tables from the settings page, and was
# invisible because a dropped block produces no error — just a shorter page.
def inline_md(frag: str) -> str:
    """Convert inline HTML to markdown. Order matters: code before emphasis, so
    a `<code>` holding asterisks is not mangled."""
    s = frag
    # Protect code spans first — their contents must not be re-processed.
    spans: list[str] = []

    def hold_code(m: re.Match) -> str:
        spans.append(m.group(1))
        return f"\x00CODE{len(spans) - 1}\x00"

    s = re.sub(r"<code[^>]*>(.*?)</code\s*>", hold_code, s, flags=re.S)

    s = re.sub(r"<a\s[^>]*href=\"([^\"]*)\"[^>]*>(.*?)</a\s*>", r"[\2](\1)", s, flags=re.S)
    s = re.sub(r"</?strong\s*>", "**", s)
    s = re.sub(r"</?b\s*>", "**", s)
    s = re.sub(r"</?em\s*>", "_", s)
    s = re.sub(r"</?i\s*>", "_", s)
    s = re.sub(r"<br\s*/?\s*>", "  \n", s)
    s = re.sub(r"<[^>]+>", "", s)          # drop any remaining tags (spans etc.)
    s = html.unescape(s)
    # Collapse ALL whitespace, newlines included. The source is hard-wrapped at
    # ~80 columns; keeping those breaks would leave ragged markdown whose
    # continuation lines carry leading spaces — and four or more of those is an
    # indented code block, so the prose would render as code.
    s = re.sub(r"\s+", " ", s).strip()

    for i, code in enumerate(spans):
        # Backticks inside a code span need a longer fence.
        text = html.unescape(re.sub(r"<[^>]+>", "", code)).strip()
        fence = "`" * (max((len(m) for m in re.findall(r"`+", text)), default=0) + 1)
        pad = " " if text.startswith("`") or text.endswith("`") else ""
        s = s.replace(f"\x00CODE{i}\x00", f"{fence}{pad}{text}{pad}{fence}")
    return s


def unwrap_svelte_literal(code: str) -> str:
    """Code blocks are written as `<pre><code>{`...`}</code></pre>` — a Svelte
    template expression. Unwrap it and undo the escaping Svelte requires."""
    s = code.strip()
    m = re.match(r"^\{\s*`(.*)`\s*\}$", s, re.S)
    if m:
        s = m.group(1)
        # Inside a template literal these are escaped; restore them.
        s = s.replace("\\`", "`").replace("\\$", "$").replace("\\\\", "\\")
    return html.unescape(re.sub(r"<[^>]+>", "", s))


def guess_lang(code: str) -> str:
    """Language for the fence.

    Order is load-bearing: the shell test is a catch-all that matches any line
    starting with a bare word, and `dmart = DmartService(...)` in a Python
    snippet begins with `dmart`. Checking the specific languages first is what
    stops Python being labelled bash. Conservative throughout — an unlabelled
    block is better than a wrongly highlighted one.
    """
    head = code.lstrip()
    if head.startswith("{") or head.startswith("["):
        return "json"
    if re.match(r"^(GET|POST|PUT|PATCH|DELETE)\s+/", head):
        return "http"
    if re.search(r"^\s*(from\s+\w+\s+import|import\s+\w+)", head, re.M) \
            or re.search(r"^\s*(def|async def)\s+\w+\(", head, re.M):
        return "python"
    if re.search(r"^\s*using\s+[\w.]+;", head, re.M) \
            or re.search(r"\b(var|await)\s+\w+\s*=\s*new\s+\w+", head):
        return "csharp"
    if re.search(r"^\s*(const|let|function|export)\b", head, re.M) \
            or "=>" in head and "console." in head:
        return "typescript"
    if re.search(r"^\s*(curl|npm|yarn|pnpm|cd|export|pip|dotnet|sudo|git|psql|\$)\b",
                 head, re.M):
        return "bash"
    if re.search(r"^\s*(SELECT|INSERT|UPDATE|CREATE|ALTER)\b", head, re.M | re.I):
        return "sql"
    return ""


def table_to_md(table_html: str) -> str:
    rows: list[list[str]] = []
    for tr in re.findall(r"<tr[^>]*>(.*?)</tr\s*>", table_html, re.S):
        cells = re.findall(r"<t[hd][^>]*>(.*?)</t[hd]\s*>", tr, re.S)
        if cells:
            rows.append([inline_md(c).replace("|", "\\|") for c in cells])
    if not rows:
        return ""
    width = max(len(r) for r in rows)
    rows = [r + [""] * (width - len(r)) for r in rows]
    out = ["| " + " | ".join(rows[0]) + " |",
           "|" + "|".join(["---"] * width) + "|"]
    for r in rows[1:]:
        out.append("| " + " | ".join(r) + " |")
    return "\n".join(out)


def list_to_md(list_html: str, ordered: bool) -> str:
    items = re.findall(r"<li[^>]*>(.*?)</li\s*>", list_html, re.S)
    out = []
    for i, it in enumerate(items, 1):
        marker = f"{i}." if ordered else "-"
        text = inline_md(it)
        if text:
            out.append(f"{marker} {text}")
    return "\n".join(out)


# ----------------------------------------------------------------- block walk
# A real traversal, not racing regexes against each other.
#
# The first version matched block patterns and took whichever hit earliest. That
# cannot see nesting, so text sitting directly inside a <div> (no <p> wrapper)
# was never emitted — it silently dropped descriptions from the drivers and
# plugins pages. A parser walks the tree, treats unknown containers as
# transparent, and therefore cannot lose a text node.

# Subtrees that are user interface, not content. Copy-to-clipboard buttons also
# carry Svelte control blocks ({#if copied === '...'}) that would otherwise leak
# into the prose as literal text.
SKIP_SUBTREE = {"button", "svg", "nav", "script", "style", "noscript"}

# Containers whose boundaries are paragraph boundaries. Treating them as
# transparent ran sibling cards together: features.md read `Flexible "Entries"
# The core unit… Structured & Unstructured Seamlessly handle…` as one paragraph.
BLOCK_CONTAINERS = {"div", "section", "article", "aside", "header", "footer", "main", "figure"}

INLINE_TAGS = {"strong", "b", "em", "i", "code", "a", "span", "br", "small", "kbd", "abbr"}

# Svelte control blocks arrive as text. Expressions are left alone: `{` appears
# legitimately in prose about JSON, and guessing which braces are code is how
# you corrupt a document.
SVELTE_CONTROL = re.compile(r"\{[#:/][^}]*\}")


class MarkdownWriter(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.blocks: list[str] = []
        self.inline: list[str] = []      # inline buffer for the current block
        self.skip_depth = 0              # >0 while inside a SKIP_SUBTREE
        self.pre: list[str] | None = None   # raw buffer while inside <pre>
        self.pre_mermaid = False
        self.pre_tag = "pre"             # the end tag that closes the raw buffer
        self.after_label = False         # just closed a <strong>/<b>
        self.heading: str | None = None  # 'h2' | 'h3' | 'h4' when open
        self.lists: list[tuple[str, int]] = []
        self.table: list[list[str]] | None = None
        self.row: list[str] | None = None
        self.in_cell = False
        self.code_depth = 0
        self.link: str | None = None

    # ---- helpers ----
    def flush_inline(self, wrapper: str = "") -> None:
        text = re.sub(r"\s+", " ", "".join(self.inline)).strip()
        self.inline = []
        if text:
            self.blocks.append(wrapper + text if wrapper else text)

    def emit(self, block: str) -> None:
        if block.strip():
            self.blocks.append(block)

    # ---- tags ----
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if self.skip_depth or tag in SKIP_SUBTREE:
            self.skip_depth += 1
            return
        if self.pre is not None:
            # Inside <pre> tags are not structure. The one that carries
            # meaning is <br/> in a mermaid label: mermaid reads it as a line
            # break, and dropping it fused "① Meta" and "identity, ownership"
            # into "① Metaidentity, ownership".
            if tag == "br" and self.pre_mermaid:
                self.pre.append("<br/>")
            return
        # `<strong>Identity</strong><span><code>user</code>…` has no
        # whitespace between label and value, so markdown would print
        # "**Identity**`user`" — two tokens fused into one word.
        if self.after_label and tag in ("span", "code", "a") \
                and self.inline and not self.inline[-1].endswith(" "):
            self.inline.append(" ")
        self.after_label = False
        # drivers.md's install commands are <div class="code-block">, not
        # <pre>: a code block in everything but the tag.
        if tag == "div" and "code-block" in (a.get("class") or "").split():
            self.flush_inline()
            self.pre = []
            self.pre_mermaid = False
            self.pre_tag = "div"
            return
        if tag == "pre":
            # Flush first: a card with "Official Python client" before its code
            # block would otherwise emit the code, then the sentence, inverting
            # the order the reader needs.
            self.flush_inline()
            self.pre = []
            self.pre_mermaid = "mermaid" in (a.get("class") or "")
            self.pre_tag = "pre"
            return
        if tag == "table":
            self.flush_inline()
            self.table = []
            return
        if tag == "tr" and self.table is not None:
            self.row = []
            return
        if tag in ("td", "th") and self.row is not None:
            self.in_cell = True
            self.inline = []
            return
        if tag in ("h1", "h2", "h3", "h4"):
            self.flush_inline()
            self.heading = tag
            return
        if tag == "p":
            self.flush_inline()
            return
        if tag in ("ul", "ol"):
            self.flush_inline()
            self.lists.append((tag, 0))
            return
        if tag == "li" and self.lists:
            self.flush_inline()
            return
        if tag == "blockquote":
            self.flush_inline()
            return
        if tag in INLINE_TAGS:
            if tag == "code":
                self.code_depth += 1
                self.inline.append("`")
            elif tag in ("strong", "b"):
                self.inline.append("**")
            elif tag in ("em", "i"):
                self.inline.append("_")
            elif tag == "br":
                self.inline.append(" ")
            elif tag == "a":
                self.link = a.get("href") or ""
                self.inline.append("[")
            return
        if tag in BLOCK_CONTAINERS and self.can_break():
            self.flush_inline()
            return
        # Other unknown tags: transparent.

    def handle_endtag(self, tag):
        if self.skip_depth:
            self.skip_depth -= 1
            return
        if self.pre is not None:
            if tag == self.pre_tag:
                raw = "".join(self.pre)
                if self.pre_mermaid:
                    self.emit("```mermaid\n" + raw.strip() + "\n```")
                else:
                    code = unwrap_svelte_literal(raw)
                    self.emit(f"```{guess_lang(code)}\n{code.rstrip()}\n```")
                self.pre = None
                self.pre_mermaid = False
            return
        if tag in ("td", "th") and self.row is not None:
            cell = re.sub(r"\s+", " ", "".join(self.inline)).strip().replace("|", "\\|")
            self.row.append(cell)
            self.inline = []
            self.in_cell = False
            return
        if tag == "tr" and self.table is not None and self.row is not None:
            self.table.append(self.row)
            self.row = None
            return
        if tag == "table" and self.table is not None:
            self.emit(rows_to_md(self.table))
            self.table = None
            return
        if tag in ("h1", "h2", "h3", "h4") and self.heading == tag:
            self.flush_inline(
                {"h1": "# ", "h2": "## ", "h3": "### ", "h4": "#### "}[tag])
            self.heading = None
            return
        if tag == "p":
            self.flush_inline()
            return
        if tag == "li" and self.lists:
            kind, n = self.lists[-1]
            n += 1
            self.lists[-1] = (kind, n)
            marker = f"{n}." if kind == "ol" else "-"
            indent = "  " * (len(self.lists) - 1)
            self.flush_inline(f"{indent}{marker} ")
            return
        if tag in ("ul", "ol") and self.lists:
            self.lists.pop()
            return
        if tag == "blockquote":
            self.flush_inline("> ")
            return
        if tag == "code":
            self.code_depth = max(0, self.code_depth - 1)
            self.inline.append("`")
            return
        if tag in ("strong", "b"):
            self.inline.append("**")
            self.after_label = True
            return
        if tag in ("em", "i"):
            self.inline.append("_")
            return
        if tag == "a":
            self.inline.append(f"]({self.link or ''})")
            self.link = None
            return
        if tag in BLOCK_CONTAINERS and self.can_break():
            self.flush_inline()
            return

    # A container boundary ends the current paragraph — unless that would cut
    # through a construct whose markdown is one line: a list item (it would
    # lose its marker), a heading, a table cell or an open link.
    def can_break(self) -> bool:
        return (not self.lists and self.heading is None and self.table is None
                and self.link is None and self.code_depth == 0)

    def handle_data(self, data):
        if self.skip_depth:
            return
        if self.pre is not None:
            self.pre.append(data)
            return
        text = SVELTE_CONTROL.sub("", data)
        if text.strip():
            self.after_label = False
        if not text.strip():
            # Keep a single space so `<strong>a</strong> <em>b</em>` does not
            # become "**a**_b_".
            if self.inline and not self.inline[-1].endswith(" "):
                self.inline.append(" ")
            return
        self.inline.append(text)

    def close_out(self) -> str:
        self.flush_inline()
        out = "\n\n".join(b.strip() for b in self.blocks if b.strip())
        return re.sub(r"\n{3,}", "\n\n", out).strip() + "\n"


def rows_to_md(rows: list[list[str]]) -> str:
    rows = [r for r in rows if any(c for c in r)]
    if not rows:
        return ""
    width = max(len(r) for r in rows)
    rows = [r + [""] * (width - len(r)) for r in rows]
    out = ["| " + " | ".join(rows[0]) + " |",
           "|" + "|".join(["---"] * width) + "|"]
    for r in rows[1:]:
        out.append("| " + " | ".join(r) + " |")
    return "\n".join(out)


def to_markdown(markup: str) -> str:
    w = MarkdownWriter()
    w.feed(markup)
    return w.close_out()


# --------------------------------------------------------------------- output
def write_space(out: Path, pages: list[dict]) -> None:
    space_dir = out / "website"
    (space_dir / ".dm").mkdir(parents=True, exist_ok=True)

    write_json(space_dir / ".dm" / "meta.space.json", {
        "uuid": det_uuid("space", "website"),
        "shortname": "website",
        "is_active": True,
        "displayname": {"en": "Website", "ar": "الموقع"},
        "description": {"en": "Public website content, published by `dmart website build`."},
        "tags": [],
        "created_at": CREATED_AT,
        "updated_at": CREATED_AT,
        "owner_shortname": "dmart",
        "languages": ["english"],
        "primary_website": "https://dmart.cc",
        "indexing_enabled": True,
        "hide_folders": [],
        "active_plugins": [],
    })

    pages_dir = space_dir / "pages"
    (pages_dir / ".dm").mkdir(parents=True, exist_ok=True)
    write_json(pages_dir / ".dm" / "meta.folder.json", {
        "uuid": det_uuid("folder", "website", "pages"),
        "shortname": "pages",
        "is_active": True,
        "tags": [],
        "created_at": CREATED_AT,
        "updated_at": CREATED_AT,
        "owner_shortname": "dmart",
        "payload": {
            "content_type": "json",
            "schema_shortname": "folder_rendering",
            "body": "pages.json",
        },
    })
    write_json(space_dir / "pages.json", {
        "shortname_title": "Page",
        "content_schema_shortnames": [],
        "index_attributes": [
            {"key": "shortname", "name": "Page"},
            {"key": "displayname", "name": "Title"},
            {"key": "slug", "name": "URL"},
        ],
        "search_columns": ["shortname", "displayname", "slug"],
        "csv_columns": ["shortname", "slug"],
        "allow_view": True,
        "allow_create": True,
        "allow_update": True,
        "allow_delete": True,
        "allow_csv": True,
        "use_media": True,
        "filter": [],
        "unique_fields": [["slug"]],
    })

    for p in pages:
        sn = p["shortname"]
        (pages_dir / ".dm" / sn).mkdir(parents=True, exist_ok=True)
        write_json(pages_dir / ".dm" / sn / "meta.content.json", {
            "uuid": det_uuid("content", "website", "pages", sn),
            "shortname": sn,
            "is_active": True,
            "slug": p["slug"],
            "displayname": {"en": p["title"]},
            "description": {"en": p["description"]},
            "tags": ["docs"],
            "created_at": CREATED_AT,
            "updated_at": CREATED_AT,
            "owner_shortname": "dmart",
            "payload": {
                "content_type": "markdown",
                "body": f"{sn}.md",
            },
        })
        (pages_dir / f"{sn}.md").write_text(p["markdown"], encoding="utf-8")


def write_json(path: Path, obj: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    # sort_keys=False keeps a human-readable field order; indent 2 matches seed/.
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


# ----------------------------------------------------------------------- main
def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--src", required=True, type=Path, help="website repo src/ directory")
    ap.add_argument("--out", required=True, type=Path, help="output directory for the space tree")
    args = ap.parse_args()

    app = args.src / "App.svelte"
    if not app.is_file():
        print(f"convert: {app} not found", file=sys.stderr)
        return 1

    pages = []
    for url, dirname, title in read_routes(app):
        page = args.src / dirname / "+page.svelte"
        if not page.is_file():
            print(f"convert: WARNING {page} missing — skipped", file=sys.stderr)
            continue
        markup = strip_blocks(page.read_text(encoding="utf-8"))

        # The lede is the first <p class="intro">; it becomes the entry's
        # description (and therefore the page's meta description and og:description).
        intro = re.search(r'<p class="intro"[^>]*>(.*?)</p\s*>', markup, re.S)
        description = inline_md(intro.group(1)) if intro else ""
        description = re.sub(r"\s+", " ", description).strip()

        md = to_markdown(markup)
        # The <h1> is dropped from the body: the generator derives the heading
        # from displayname, and keeping both would print the title twice.
        md = re.sub(r"^#\s+.*\n+", "", md, count=1)

        slug = url.lstrip("/")
        pages.append({
            "shortname": slug.replace("-", "_"),
            "slug": slug,
            "title": title,
            "description": description,
            "markdown": md,
        })

    write_space(args.out, pages)
    print(f"convert: wrote {len(pages)} page(s) to {args.out}/website")
    for p in pages:
        print(f"  {p['shortname']:<20} /{p['slug']:<20} {len(p['markdown']):>6} chars  {p['title']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
