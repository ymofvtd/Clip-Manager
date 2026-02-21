#!/usr/bin/env python3
import argparse
import os
import random
import string
import sys
import time
from pathlib import Path

# Default video extensions
DEFAULT_VIDEO_EXTS = {
    ".mp4", ".mov", ".m4v", ".mxf", ".avi", ".mkv", ".wmv",
    ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".flv", ".webm"
}

ALPHABET = string.ascii_letters + string.digits 

def gen_id(length: int) -> str:
    return "".join(random.choice(ALPHABET) for _ in range(length))

def list_files(folder: Path, include_all: bool, extra_exts):
    files = []
    for p in folder.iterdir():
        if not p.is_file() or p.name.startswith("."): 
            continue
        if include_all:
            files.append(p)
        else:
            exts = set(e.lower().strip() for e in extra_exts) if extra_exts else set()
            exts |= set(ext.lower() for ext in DEFAULT_VIDEO_EXTS)
            if p.suffix.lower() in exts:
                files.append(p)
    return files

def ensure_unique(target: Path) -> Path:
    if not target.exists():
        return target
    stem, suffix = target.stem, target.suffix
    parent = target.parent
    i = 1
    while True:
        candidate = parent / f"{stem}__{i}{suffix}"
        if not candidate.exists():
            return candidate
        i += 1

def main():
    parser = argparse.ArgumentParser(
        description="Two-pass rename: Shuffle -> Numbers (Step 10, No Padding)."
    )
    parser.add_argument("folder", type=str, help="Folder containing files")
    parser.add_argument("start", type=int, nargs="?", default=0,
                        help="Starting number (default: 0)")
    
    parser.add_argument("--all", action="store_true", help="Include ALL files")
    parser.add_argument("--ext", action="append", default=[], help="Extra extensions")
    parser.add_argument("--length", type=int, default=10, help="Random ID length")
    parser.add_argument("--prefix", type=str, default="", help="Filename prefix")
    parser.add_argument("--pad", type=int, default=0, help="Manual padding width (0 for none)")
    parser.add_argument("--seed", type=int, default=None, help="Random seed")
    parser.add_argument("--dry-run", action="store_true", help="Preview changes")
    args = parser.parse_args()

    if args.seed is not None:
        random.seed(args.seed)

    folder = Path(args.folder).resolve()
    if not folder.exists() or not folder.is_dir():
        print(f"Error: '{folder}' is not a directory.", file=sys.stderr)
        sys.exit(1)

    files = list_files(folder, args.all, args.ext)
    files = sorted(files) 
    if not files:
        print("No files found to process.")
        sys.exit(0)

    total = len(files)
    stage1_map = [] 
    used_ids = set()

    print(f"{'[DRY RUN] ' if args.dry_run else ''}Stage 1: Shuffling {total} files...")

    for src in files:
        while True:
            rid = gen_id(args.length)
            if rid not in used_ids:
                used_ids.add(rid)
                break
        dst = src.with_name(f"{rid}{src.suffix}")
        if dst.exists():
            dst = ensure_unique(dst)
        stage1_map.append((src, dst))
        if not args.dry_run:
            src.rename(dst)

    if args.dry_run:
        randomized_files = sorted([dst for (_src, dst) in stage1_map], key=lambda p: p.name)
    else:
        produced_names = {dst.name for (_src, dst) in stage1_map}
        randomized_files = sorted([p for p in folder.iterdir()
                                   if p.is_file() and p.name in produced_names], key=lambda p: p.name)

    now = time.strftime("%Y%m%d-%H%M%S")
    log_path = folder / f"rename_mapping_{now}.csv"
    
    print(f"{'[DRY RUN] ' if args.dry_run else ''}Stage 2: Renaming with step 10 (start={args.start})")

    if not args.dry_run:
        with open(log_path, "w", encoding="utf-8") as f:
            f.write("original,random,final\n")

    orig_by_random = {dst.name: src.name for (src, dst) in stage1_map}
    seq = args.start
    for current in randomized_files:
        # If pad is 0, it just converts the number to a normal string (1, 10, 20...)
        num_str = str(seq).zfill(args.pad) if args.pad > 0 else str(seq)
        target = current.with_name(f"{args.prefix}{num_str}{current.suffix}")

        if target.exists() and target.name != current.name:
            target = ensure_unique(target)

        if args.dry_run:
            print(f"[DRY RUN] {current.name} -> {target.name}")
        else:
            current.rename(target)
            with open(log_path, "a", encoding="utf-8") as f:
                original_name = orig_by_random.get(current.name, "")
                f.write(f"{original_name},{current.name},{target.name}\n")

        seq += 10 

    print("\nDone.")
    if not args.dry_run:
        print(f"Mapping saved to: {log_path}")

if __name__ == "__main__":
    main()