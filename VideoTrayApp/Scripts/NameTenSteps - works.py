#!/usr/bin/env python3
import argparse
import os
import random
import string
import sys
import re
from pathlib import Path

DEFAULT_VIDEO_EXTS = {
    ".mp4", ".mov", ".m4v", ".mxf", ".avi", ".mkv", ".wmv",
    ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".flv", ".webm"
}

def gen_id(length: int) -> str:
    """Generates a random string for the safety pass."""
    alphabet = string.ascii_letters + string.digits
    return "".join(random.choice(alphabet) for _ in range(length))

def natural_sort_key(s):
    """Sorts strings containing numbers in the way a human would (1, 2, 10, 20)."""
    return [int(text) if text.isdigit() else text.lower()
            for text in re.split(r'(\d+)', str(s))]

def list_files(folder: Path):
    """Filters files for common video extensions."""
    files = []
    for p in folder.iterdir():
        if not p.is_file() or p.name.startswith("."): 
            continue
        if p.suffix.lower() in DEFAULT_VIDEO_EXTS:
            files.append(p)
    return files

def main():
    parser = argparse.ArgumentParser(description="Consistently rename files by 10s.")
    parser.add_argument("folder", type=str, help="Folder containing files")
    parser.add_argument("start", type=int, nargs="?", default=0, help="Starting number")
    parser.add_argument("--dry-run", action="store_true", help="Preview changes")
    args = parser.parse_args()

    folder = Path(args.folder).resolve()
    # Apply natural sort so 20.mp4 comes BEFORE 100.mp4
    files = sorted(list_files(folder), key=natural_sort_key)
    
    if not files:
        print("No files found.")
        sys.exit(0)

    # Pass 1: Move to TEMP names to clear the namespace
    temp_map = []
    if not args.dry_run:
        print(f"Preparing {len(files)} files...")
        for src in files:
            temp_path = src.with_name(f"TEMP_{gen_id(12)}{src.suffix}")
            src.rename(temp_path)
            temp_map.append((src.name, temp_path))
    else:
        for src in files:
            temp_map.append((src.name, src))

    # Pass 2: Final naming with step 10
    seq = args.start
    print(f"{'[DRY RUN] ' if args.dry_run else ''}Renaming...")
    
    for original_name, current_path in temp_map:
        target_name = f"{seq}{current_path.suffix}"
        target_path = current_path.with_name(target_name)
        
        print(f"{original_name} -> {target_name}")
        if not args.dry_run:
            current_path.rename(target_path)
        
        seq += 10

    print("\nDone.")

if __name__ == "__main__":
    main()