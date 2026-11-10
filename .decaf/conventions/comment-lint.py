"""Flags comment wording that comments.md's rules usually forbid, for a reader to judge.

Usage: python3 .decaf/conventions/comment-lint.py <file or folder>...

It reads // and /// comments in .cs files, and // comments in .ts, .svelte, .mjs and .json files. Each line it prints is a candidate, not a verdict: the rules allow some of these
in context, such as the passive for an unknown actor.
"""

import html
import pathlib
import re
import sys

PARTICLES = r"(?:up|down|out|off|back|over|away|through|around|ahead|apart)"
PHRASAL_VERBS = re.compile(
    r"\b(?:give|gives|gave|go|goes|went|hold|holds|held|back|backs|backed|cut|cuts|set|sets|take|takes|took|fall|falls|fell|run|runs|ran|show|shows|showed|"
    r"turn|turns|turned|pick|picks|picked|hand|hands|handed|come|comes|came|carry|carries|carried|bring|brings|brought|put|puts|figure|figures|figured|"
    r"end|ends|ended|wind|winds|wound|start|starts|started|check|checks|checked|fill|fills|filled|sort|sorts|sorted|work|works|worked|keep|keeps|kept|"
    r"leave|leaves|left|make|makes|made|point|points|pointed|spell|spells|spelled|split|splits|break|breaks|broke|let|lets|clean|cleans|cleaned)\s+"
    + PARTICLES + r"\b(?!\s+(?:of|to|from|the|a|an)\b)",
    re.IGNORECASE)
CHECKS = [
    ("phrasal verb", PHRASAL_VERBS),
    ("two readings: 'in any case'", re.compile(r"\bin any case\b", re.IGNORECASE)),
    ("passive with an agent", re.compile(r"\b(?:is|are|was|were|be|been|being)\s+\w+ed\s+by\b", re.IGNORECASE)),
    ("sentence opens with a participle, so it may lack a subject", re.compile(r"(?:^|[.!?]\s+)(?!Indexed\b|Unchecked\b|Needed\b)([A-Z][a-z]+ed)\s+(?:by|when|if|until|before|after|for|to|in|on)\b")),
    ("semicolon chain", re.compile(r";\s+[a-z]")),
    ("history", re.compile(r"\b(?:previously|used to|renamed from|migrated from|replaces the old)\b", re.IGNORECASE)),
    ("reference to a decision or nib", re.compile(r"\b(?:decision \d+\.\d+|lots-[a-z0-9]{4}|phase \d+)\b", re.IGNORECASE)),
]
COMMENT = re.compile(r"^\s*(?:///?)\s?(.*)$")


def comments(path):
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        match = COMMENT.match(line)
        if match:
            # Decoding after the tag pass keeps an escaped <c>&lt;name&gt;</c> as text.
            yield number, html.unescape(re.sub(r"<[^>]+>", " ", match.group(1)))


def main(arguments):
    paths = []
    for argument in arguments or ["."]:
        root = pathlib.Path(argument)
        paths += [root] if root.is_file() else [p for p in root.rglob("*") if p.suffix in (".cs", ".ts", ".svelte", ".mjs", ".json") and not {"bin", "obj", "node_modules", "dist"} & set(p.parts)]
    found = 0
    for path in sorted(paths):
        for number, text in comments(path):
            for name, pattern in CHECKS:
                match = pattern.search(text)
                if match:
                    found += 1
                    print(f"{path}:{number}: {name}: {match.group(0).strip()!r}")
    print(f"{found} candidates", file=sys.stderr)


if __name__ == "__main__":
    main(sys.argv[1:])
