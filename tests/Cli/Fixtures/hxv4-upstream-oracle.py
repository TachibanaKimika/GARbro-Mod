#!/usr/bin/env python3
"""Run the audited hxv4_unhash_tools PlainDict sources over local fixtures.

This helper intentionally imports the separately checked-out upstream tree.
It does not vendor or redistribute upstream source or binaries.
"""

import argparse
import contextlib
import io
import json
import re
import sys
import types
from pathlib import Path
from types import SimpleNamespace


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream-root", required=True)
    parser.add_argument("--source-root", required=True)
    parser.add_argument("--krkrdump-root", required=True)
    args = parser.parse_args()

    upstream_root = Path(args.upstream_root).resolve()
    source_root = Path(args.source_root).resolve()
    krkrdump_root = Path(args.krkrdump_root).resolve()
    sys.path.insert(0, str(upstream_root))

    # The audited project imports the small third-party json5 package only for
    # base.stage. Keep the oracle self-contained by supplying the subset its
    # converter emits for these deterministic fixtures.
    try:
        import json5  # noqa: F401
    except ModuleNotFoundError:
        json5_module = types.ModuleType("json5")

        def loads(value):
            value = value.lstrip("\ufeff")
            value = re.sub(
                r"([{,]\s*)([A-Za-z_]\w*)\s*:",
                lambda match: f'{match.group(1)}"{match.group(2)}":',
                value,
            )
            value = re.sub(r",(\s*[}\]])", r"\1", value)
            return json.loads(value)

        json5_module.loads = loads
        sys.modules["json5"] = json5_module

    from plain_dict import PlainDict

    # PlainDict stores these sets at class scope. Reset them so every oracle
    # invocation is deterministic even when hosted by a reused interpreter.
    PlainDict.pathname_plaintexts = set()
    PlainDict.filename_plaintexts = set()

    oracle_temp = krkrdump_root / ".upstream-oracle"
    oracle_temp.mkdir(parents=True, exist_ok=True)
    config = SimpleNamespace(
        psbdecompile_exe=upstream_root
        / "binaries"
        / "psb_decompile"
        / "PsbDecompile.exe",
        pbd2json_exe=upstream_root / "binaries" / "pbd2json.exe",
        temp_dir=oracle_temp,
        psb_type_cache_pkl=oracle_temp / "psb_type_cache.pkl",
        rename_dir=source_root,
    )

    data_main = source_root / "data" / "main"
    voice = source_root / "voice"
    fgimage = source_root / "fgimage"
    scenario = source_root / "scn"
    dictionary = PlainDict(
        config=config,
        pathnames=["/"],
        filenames=[
            "base.stage",
            "cglist.csv",
            "soundlist.csv",
            "charvoice.csv",
            "imagediffmap.csv",
            "savelist.csv",
            "scenelist.csv",
            "replay.ks",
            "_chthum_index.pbd",
        ],
    )

    # Upstream methods are intentionally noisy. Keep stdout machine-readable
    # and use only the resulting candidate sets as the behavioral oracle.
    with contextlib.redirect_stdout(io.StringIO()):
        (
            dictionary.from_unobfuscated_directory(str(source_root))
            .scan_psb_and_decompile(str(scenario))
            .from_base_stage(str(data_main / "base.stage"))
            .from_cglist_csv(str(data_main / "cglist.csv"))
            .from_soundlist_csv(str(data_main / "soundlist.csv"))
            .from_krkrdump_logs(str(krkrdump_root))
            .add_char_sys_voices(str(data_main / "charvoice.csv"))
            .from_imagediffmap_csv(str(data_main / "imagediffmap.csv"))
            .from_bgv_csv(str(voice))
            .from_savelist_csv(str(data_main / "savelist.csv"))
            .from_scenelist_csv(str(data_main / "scenelist.csv"))
            .find_missing_voices([str(voice)])
            .add_movies(str(data_main / "replay.ks"))
            .from_stand_files(str(fgimage))
            .from_pbd_files(str(fgimage))
            .from_chthum_index_pbd(str(fgimage / "_chthum_index.pbd"))
            .duplicate_lower()
        )

    print(
        json.dumps(
            {
                "files": sorted(dictionary.filename_plaintexts),
                "paths": sorted(dictionary.pathname_plaintexts),
            },
            ensure_ascii=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
