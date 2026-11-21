"""Generate the QA servers' "QA Search" titles: a few thousand films and some shows as .strm + .nfo pairs (seed.ps1).

    python -I -B search_seed.py --out %USERPROFILE%\\Dev\\jellyfin-qa\\search-media --target <a test-pattern .mkv> [--list]

Nixie 1.5 (the search stream): search needs a library big enough to show how each server matches and ranks
(docs/research/search.md), with near-misses around the titles people type ("It's Always Fair Weather" beside "It's
Always Sunny in Philadelphia"). Each title is a .strm file (one line: the path of an existing test-pattern file, so it
plays) beside an .nfo that gives its name, original title, sort title, year and cast: the server reads them with its
local NFO reader, and the libraries are added with every internet metadata and image fetcher off, so no title is looked
up anywhere. The film and show names are generated from word lists with a fixed seed (the same titles every run), plus a
fixed list of real titles that matter to search.

Idempotent: <out>\\search.json keeps the spec's hash; a file is written only when missing or changed, files not in the
spec are removed (only inside <out>, which holds nothing else). Stdlib only.
"""

import argparse
import hashlib
import json
import os
import random
import sys
from xml.sax.saxutils import escape

SPEC_VERSION = 1
FILMS = 3000          # generated films, besides the fixed ones
SHOWS = 24            # generated shows, besides the fixed ones

# the titles that matter to search (name, year, original title or None, sort title or None)
FIXED_FILMS = [
    ("It's Always Fair Weather", 1955, None, None),
    ("Always", 1989, None, None),
    ("Sunny", 2011, "써니", None),
    ("It", 2017, None, None),
    ("It's a Wonderful Life", 1946, None, None),
    ("Little Miss Sunshine", 2006, None, None),
    ("Eternal Sunshine of the Spotless Mind", 2004, None, None),
    ("Always Be My Maybe", 2019, None, None),
    ("It Follows", 2014, None, None),
    ("It Happened One Night", 1934, None, None),
    ("Sunny Side Up", 1929, None, None),
    ("The Sunny Side of the Street", 1951, None, None),
    ("Philadelphia", 1993, None, None),
    ("The Philadelphia Story", 1940, None, None),
    ("M*A*S*H", 1970, None, None),
    ("Se7en", 1995, None, None),
    ("WALL·E", 2008, None, None),
    ("Amélie", 2001, "Le Fabuleux Destin d'Amélie Poulain", None),
    ("Schindler's List", 1993, None, None),
    ("2001: A Space Odyssey", 1968, None, None),
    ("Star Wars: A New Hope", 1977, None, "Star Wars 4"),
    ("Spider-Man: No Way Home", 2021, None, None),
    ("Crouching Tiger, Hidden Dragon", 2000, "臥虎藏龍", None),
    ("Spirited Away", 2001, "千と千尋の神隠し", None),
    ("Pokémon: The First Movie", 1998, None, None),
    ("Up", 2009, None, None),
    ("Upgrade", 2018, None, None),
    ("Dune", 2021, None, None),
    ("Dune: Part Two", 2024, None, None),
    ("Toy Story", 1995, None, None),
    ("Toy Story 2", 1999, None, None),
    ("A Quiet Place", 2018, None, None),
    ("Office Space", 1999, None, None),
    ("Ocean's Eleven", 2001, None, None),
    ("Ocean's Twelve", 2004, None, None),
]

# (name, year, [(season, [episode titles])])
FIXED_SHOWS = [
    ("It's Always Sunny in Philadelphia", 2005, [
        (1, ["The Gang Gets Started", "Charlie Wants an Abortion", "Underage Drinking", "Charlie Has Cancer",
             "Gun Fever", "The Gang Finds a Dead Guy", "Charlie Got Molested"]),
        (2, ["Charlie Gets Crippled", "The Gang Goes Jihad", "Dennis and Dee Go on Welfare", "Mac Bangs Dennis' Mom",
             "Hundred Dollar Baby", "The Gang Gives Back"]),
    ]),
    ("M*A*S*H", 1972, [(1, ["The Pilot", "To Market, to Market", "Requiem for a Lightweight"])]),
    ("Sunny Days", 2017, [(1, ["Always Sunny", "It's a Sunny Day", "Rain"])]),
    ("The Office", 2005, [(1, ["Pilot", "Diversity Day", "Health Care"])]),
    ("Always Sunny Somewhere", 2020, [(1, ["Gang Gets Lost", "Philadelphia Freedom"])]),
]

ADJ = ["Silent", "Broken", "Golden", "Hidden", "Last", "Lost", "Dark", "Bright", "Sunny", "Cold", "Wild", "Quiet",
       "Final", "Burning", "Midnight", "Electric", "Frozen", "Crimson", "Velvet", "Iron", "Paper", "Glass", "Hollow",
       "Endless", "Secret", "Little", "Long", "Distant", "Fading", "Sweet", "Bitter", "Rising", "Falling", "Sleeping"]
NOUN = ["River", "Summer", "Night", "City", "Witness", "Garden", "Kingdom", "Station", "Harbor", "Mountain", "Road",
        "Promise", "Shadow", "Letter", "Island", "Season", "Mirror", "Engine", "Window", "Highway", "Orchard", "Signal",
        "Lantern", "Horizon", "Weather", "Morning", "Stranger", "Echo", "Dream", "Storm", "Heart", "Circus", "Bridge",
        "Planet", "Desert", "Forest", "Machine", "Company", "Family", "Sunshine"]
PLACE = ["Philadelphia", "Paris", "Tokyo", "Avalon", "Brooklyn", "the North", "the Valley", "Marrakesh", "Oslo",
         "Rio", "Memphis", "the Coast", "Lisbon", "Nowhere", "the Moon"]
NAME = ["Charlie", "Mac", "Dee", "Frank", "Dennis", "Maria", "Jun", "Amélie", "Søren", "Zoë", "Björn", "Ines", "Rosa",
        "Kofi", "Priya", "Mateo", "Hana", "Ola", "Sunny", "Ivy"]
VERB = ["Running", "Waiting", "Falling", "Dancing", "Leaving", "Chasing", "Finding", "Burning", "Singing", "Breaking"]
FIRST = ["Ava", "Ben", "Cleo", "Dev", "Elio", "Fern", "Gus", "Hal", "Iris", "Jules", "Kit", "Lena", "Milo", "Nell",
         "Otto", "Pia", "Quinn", "Rhea", "Sol", "Tess", "Uma", "Vik", "Wren", "Xia", "Yara", "Zed", "Sunny", "Danny"]
LAST = ["Abara", "Brooks", "Calder", "Duarte", "Ellery", "Fontaine", "Garza", "Holm", "Ibarra", "Jansen", "Kowal",
        "Laine", "Marsh", "Novak", "Okafor", "Price", "Quill", "Reyes", "Sato", "Thorne", "Ueda", "Vance", "Weller",
        "Always", "Philips"]
INVALID = '<>:"/\\|?*'


def safe(name):
    """A file name for a title (Windows refuses <>:"/\\|?*); the .nfo carries the real one."""
    out = "".join("-" if c in INVALID else c for c in name).strip().rstrip(".")
    return out or "untitled"


def generated_films(rng):
    patterns = [
        lambda: "%s %s" % (rng.choice(ADJ), rng.choice(NOUN)),
        lambda: "The %s %s" % (rng.choice(ADJ), rng.choice(NOUN)),
        lambda: "%s of %s" % (rng.choice(NOUN), rng.choice(PLACE)),
        lambda: "A %s in %s" % (rng.choice(NOUN), rng.choice(PLACE)),
        lambda: "%s's %s" % (rng.choice(NAME), rng.choice(NOUN)),
        lambda: "%s %s" % (rng.choice(VERB), rng.choice(NOUN)),
        lambda: "%s %d" % (rng.choice(NOUN), rng.randint(2, 4)),
        lambda: "It Came from %s" % rng.choice(PLACE),
        lambda: "Always %s" % rng.choice(VERB),
        lambda: "%s, %s" % (rng.choice(NAME), rng.choice(PLACE)),
    ]
    # a name at most twice (a remake: another year), never the name of a fixed title
    uses = {f[0].lower(): 2 for f in FIXED_FILMS}
    films = []
    while len(films) < FILMS:
        name = rng.choice(patterns)()
        year = rng.randint(1930, 2025)
        key = name.lower()
        if uses.get(key, 0) >= 2 or any(f[0].lower() == key and f[1] == year for f in films):
            continue
        uses[key] = uses.get(key, 0) + 1
        films.append((name, year, None, None))
    return films


def generated_shows(rng):
    seen = {s[0].lower() for s in FIXED_SHOWS}
    shows = []
    while len(shows) < SHOWS:
        name = "%s %s" % (rng.choice(ADJ), rng.choice(NOUN))
        if name.lower() in seen:
            continue
        seen.add(name.lower())
        episodes = ["%s %s" % (rng.choice(VERB), rng.choice(NOUN)) for _ in range(3)]
        shows.append((name, rng.randint(1990, 2025), [(1, episodes)]))
    return shows


# forty people only: a new person is looked up by the server's own people refresh, so the cast stays small
PEOPLE = ["%s %s" % (FIRST[i % len(FIRST)], LAST[(i * 7) % len(LAST)]) for i in range(40)]


def actors(rng, count):
    return rng.sample(PEOPLE, count)


def nfo_movie(name, year, original, sort, cast):
    lines = ["<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", "<movie>",
             "  <title>%s</title>" % escape(name), "  <year>%d</year>" % year]
    if original:
        lines.append("  <originaltitle>%s</originaltitle>" % escape(original))
    if sort:
        lines.append("  <sorttitle>%s</sorttitle>" % escape(sort))
    lines.append("  <plot>A Nixie QA search title. Test pattern only.</plot>")
    for actor in cast:
        lines.append("  <actor><name>%s</name><role>Self</role><type>Actor</type></actor>" % escape(actor))
    lines.append("</movie>")
    return "\n".join(lines) + "\n"


def spec(target):
    """Every file to write: relative path -> text."""
    rng = random.Random(150)
    files = {}
    for name, year, original, sort in FIXED_FILMS + generated_films(rng):
        stem = "%s (%d)" % (safe(name), year)
        folder = "Movies/%s" % stem
        files["%s/%s.strm" % (folder, stem)] = target + "\n"
        files["%s/%s.nfo" % (folder, stem)] = nfo_movie(name, year, original, sort, actors(rng, 2))
    for name, year, seasons in FIXED_SHOWS + generated_shows(rng):
        folder = "Shows/%s (%d)" % (safe(name), year)
        files["%s/tvshow.nfo" % folder] = "\n".join([
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", "<tvshow>",
            "  <title>%s</title>" % escape(name), "  <year>%d</year>" % year,
            "  <plot>A Nixie QA search show. Test pattern only.</plot>",
            *["  <actor><name>%s</name><role>Self</role><type>Actor</type></actor>" % escape(a) for a in actors(rng, 3)],
            "</tvshow>"]) + "\n"
        for season, titles in seasons:
            for index, title in enumerate(titles, 1):
                stem = "%s S%02dE%02d" % (safe(name), season, index)
                base = "%s/Season %02d/%s" % (folder, season, stem)
                files[base + ".strm"] = target + "\n"
                files[base + ".nfo"] = "\n".join([
                    "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", "<episodedetails>",
                    "  <title>%s</title>" % escape(title), "  <season>%d</season>" % season,
                    "  <episode>%d</episode>" % index, "</episodedetails>"]) + "\n"
    return files


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", required=True)
    ap.add_argument("--target", required=True, help="the test-pattern file every .strm points at")
    ap.add_argument("--list", action="store_true", help="print the titles and write nothing")
    args = ap.parse_args()
    files = spec(os.path.abspath(args.target))
    films = sum(1 for p in files if p.startswith("Movies/") and p.endswith(".strm"))
    episodes = sum(1 for p in files if p.startswith("Shows/") and p.endswith(".strm"))
    shows = sum(1 for p in files if p.endswith("tvshow.nfo"))
    if args.list:
        for p in sorted(files):
            if p.endswith(".strm"):
                print(p)
        print("search-media: %d films, %d shows, %d episodes" % (films, shows, episodes))
        return 0
    out = os.path.abspath(args.out)
    # review (m8): strays are removed below, so only in a folder of its own: empty, or one it made (search.json in it)
    if os.path.isdir(out) and os.listdir(out) and not os.path.isfile(os.path.join(out, "search.json")):
        print("search-media: %s holds other files and no search.json: refusing to write (and delete) there" % out,
              file=sys.stderr, flush=True)
        return 2
    os.makedirs(out, exist_ok=True)
    written = kept = removed = 0
    for rel, text in files.items():
        path = os.path.join(out, *rel.split("/"))
        data = text.encode("utf-8")
        try:
            with open(path, "rb") as f:
                if f.read() == data:
                    kept += 1
                    continue
        except OSError:
            pass
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
        written += 1
    wanted = {os.path.normcase(os.path.join(out, *rel.split("/"))) for rel in files}
    wanted.add(os.path.normcase(os.path.join(out, "search.json")))
    for d, _, names in os.walk(out):
        for n in names:
            p = os.path.join(d, n)
            if os.path.normcase(p) not in wanted:
                os.remove(p)
                removed += 1
    digest = hashlib.sha256(json.dumps(sorted(files.items()), ensure_ascii=False).encode("utf-8")).hexdigest()
    manifest = {"version": SPEC_VERSION, "films": films, "shows": shows, "episodes": episodes, "sha256": digest}
    with open(os.path.join(out, "search.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print("search-media: %d films, %d shows, %d episodes; %d written, %d kept, %d stray removed" % (
        films, shows, episodes, written, kept, removed), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
