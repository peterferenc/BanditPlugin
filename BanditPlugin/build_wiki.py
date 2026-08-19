#!/usr/bin/env python3
"""Split README.md into GitHub wiki pages.

README.md stays the source of truth; this regenerates wiki/ from it so the two
can never drift. Run from the BanditPlugin folder:  python3 build_wiki.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
README = os.path.join(HERE, "README.md")
OUT = os.path.join(HERE, "wiki")

# (wiki page name, sidebar label, README "## " headings that belong on it)
PAGES = [
    ("Commands", "Commands", ["Commands"]),
    ("Architecture", "Architecture", ["How it works"]),
    ("Movement-Cover-and-Patrol", "Movement, cover & patrol", ["Movement, cover and patrol"]),
    ("Vehicles", "Vehicles", ["Vehicles"]),
    ("Convoys", "Convoys", ["Convoys"]),
    ("Teams", "Teams", ["Teams"]),
    ("Engine-Workarounds", "Engine workarounds",
     ["Non-obvious things this had to work around"]),
    ("Why-Not-Zombies", "Why not zombies?", ["Why not zombies?"]),
    ("Build-and-Install", "Build & install", ["Build", "Install"]),
    ("Configuration", "Configuration", ["Configuration"]),
    ("Known-Limitations", "Known limitations", ["Known limitations"]),
]

GROUPS = [
    ("Using it", ["Commands", "Configuration", "Build-and-Install"]),
    ("How it works", ["Architecture", "Movement-Cover-and-Patrol", "Vehicles",
                      "Convoys", "Teams"]),
    ("Background", ["Engine-Workarounds", "Why-Not-Zombies", "Known-Limitations"]),
]

# Vehicles is long and covers two separate subjects; cut it at this "### " heading.
SPLIT_VEHICLES_AT = "Firing a turret"
TURRET_PAGE = ("Turrets", "Turrets", [])

# Prose that pointed "below" inside one long README now has to point at a page.
CROSSREFS = [
    (r'See "Why not zombies\?" below\.', 'See [Why not zombies?](Why-Not-Zombies).'),
    (r'See the range limitation below\.', 'See [Known limitations](Known-Limitations).'),
]


def read_sections(text):
    """README -> {heading: body}, preserving order, for '## ' headings only."""
    parts = re.split(r'^## (.+)$', text, flags=re.M)
    intro = parts[0]
    sections = {}
    order = []
    for i in range(1, len(parts), 2):
        heading = parts[i].strip()
        sections[heading] = parts[i + 1].strip("\n")
        order.append(heading)
    return intro, sections, order


def promote(body):
    """A wiki page is its own document: '###' becomes '##', and so on."""
    return re.sub(r'^###', '##', body, flags=re.M)


def crossref(text):
    for pattern, replacement in CROSSREFS:
        text = re.sub(pattern, replacement, text)
    return text


def sidebar():
    lines = ["### BanditPlugin", "", "[Home](Home)", ""]
    for group, names in GROUPS:
        lines.append(f"**{group}**")
        lines.append("")
        for name in names:
            label = next(l for n, l, _ in ALL_PAGES if n == name)
            lines.append(f"- [{label}]({name})")
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def home(intro, credits):
    lines = [intro.strip(), "", "## Contents", ""]
    for group, names in GROUPS:
        lines.append(f"**{group}**")
        lines.append("")
        for name in names:
            label = next(l for n, l, _ in ALL_PAGES if n == name)
            lines.append(f"- **[{label}]({name})**")
        lines.append("")
    lines.append("## Credits")
    lines.append("")
    lines.append(credits.strip())
    return crossref("\n".join(lines)).rstrip() + "\n"


def main():
    global ALL_PAGES
    with open(README, encoding="utf-8") as handle:
        text = handle.read()

    intro, sections, order = read_sections(text)

    missing = [h for _, _, hs in PAGES for h in hs if h not in sections]
    if missing:
        sys.exit(f"README has no '## ' section for: {', '.join(missing)}")

    os.makedirs(OUT, exist_ok=True)

    # Vehicles carries both driving and turret gunnery; give each its own page.
    vehicles = sections["Vehicles"]
    cut = vehicles.find(f"### {SPLIT_VEHICLES_AT}")
    if cut == -1:
        sys.exit(f"Vehicles section has no '### {SPLIT_VEHICLES_AT}' to split on")
    sections["Vehicles"] = vehicles[:cut].rstrip()
    sections["Turrets"] = vehicles[cut:].rstrip()

    pages = list(PAGES)
    pages.insert(pages.index(("Convoys", "Convoys", ["Convoys"])),
                 (TURRET_PAGE[0], TURRET_PAGE[1], ["Turrets"]))
    ALL_PAGES = pages
    GROUPS[1] = ("How it works", ["Architecture", "Movement-Cover-and-Patrol",
                                  "Vehicles", "Turrets", "Convoys", "Teams"])

    written = []
    for name, label, headings in pages:
        # A page built from one README section takes that section's own name; a page
        # that merges several keeps each as a heading, or the bodies run together.
        if len(headings) == 1:
            title = headings[0]
            body = promote(sections[headings[0]])
        else:
            title = label
            body = "\n\n".join(f"## {h}\n\n{promote(sections[h])}" for h in headings)
        page = f"# {title}\n\n{crossref(body).strip()}\n"
        path = os.path.join(OUT, f"{name}.md")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(page)
        written.append((f"{name}.md", len(page.splitlines())))

    for filename, content in (("Home.md", home(intro, sections["Credits"])),
                              ("_Sidebar.md", sidebar())):
        with open(os.path.join(OUT, filename), "w", encoding="utf-8") as handle:
            handle.write(content)
        written.append((filename, len(content.splitlines())))

    for filename, lines in written:
        print(f"{lines:5d}  wiki/{filename}")


if __name__ == "__main__":
    main()
