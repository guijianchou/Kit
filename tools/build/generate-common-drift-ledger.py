"""Compare tracked Kit common files with a local, read-only PowerToys snapshot.

No source, Git state, packages or configuration is changed. The default only prints
the summary. --write refreshes the four named report files; --check compares their
current bytes with a fresh result without writing. Uses Python's standard library.
"""

from __future__ import annotations

import argparse
import csv
import difflib
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


BRAND_MAP = (('PowerToys', 'Kit'), ('powertoys', 'kit'), ('Powertoys', 'Kit'))
CORE_REFERENCE_PATHS = (
    'Cpp.Build.props', 'Cpp.Build.targets', 'Directory.Build.props',
    'Directory.Build.targets', 'Directory.Packages.props', 'src/Version.props',
    'src/runner/runner.vcxproj', 'src/runner/main.cpp',
    'src/runner/powertoy_module.h', 'src/runner/powertoy_module.cpp',
    'src/runner/auto_start_helper.cpp', 'src/runner/settings_window.cpp',
    'src/modules/interface/powertoy_module_interface.h',
    'src/settings-ui/Settings.UI/PowerToys.Settings.csproj',
    'src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml',
)
CATEGORIES = ('byte-equal', 'normalized-equal', 'brand-only', 'content-diff', 'binary-diff')


def git(repo: Path, *args: str) -> bytes:
    return subprocess.check_output(['git', '-C', str(repo), *args])


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def normalize(data: bytes) -> str | None:
    try:
        return data.decode('utf-8-sig').replace('\r\n', '\n').replace('\r', '\n')
    except UnicodeDecodeError:
        return None


def brand_normalize(text: str) -> str:
    for old, new in BRAND_MAP:
        text = text.replace(old, new)
    return text


def compact(text: str) -> str:
    # A TSV row stays on one physical line. This is a preview, not a patch.
    return text.replace('\t', '\\t').replace('\r', '\\r').replace('\n', '\\n')[:180]


def diff_stats(reference: str, kit: str) -> tuple[int, int, str, str]:
    upstream_lines, kit_lines = reference.splitlines(), kit.splitlines()
    added = removed = 0
    first_reference = first_kit = ''
    first = True
    for tag, i, j, k, m in difflib.SequenceMatcher(
        None, upstream_lines, kit_lines, autojunk=False
    ).get_opcodes():
        if tag == 'equal':
            continue
        removed += j - i
        added += m - k
        if first:
            first_reference = compact(upstream_lines[i]) if j > i else ''
            first_kit = compact(kit_lines[k]) if m > k else ''
            first = False
    return added, removed, first_reference, first_kit


def tsv(fields: list[str], rows: list[dict]) -> bytes:
    stream = io.StringIO(newline='')
    writer = csv.DictWriter(stream, fieldnames=fields, delimiter='\t', lineterminator='\n')
    writer.writeheader()
    writer.writerows(rows)
    return stream.getvalue().encode('utf-8')


def version_fields(path: Path) -> dict:
    root = ET.parse(path).getroot()
    wanted = {'Version', 'VersionChannel', 'ReleaseTrainVersion', 'SourceCommit'}
    return {
        node.tag.rsplit('}', 1)[-1]: (node.text or '').strip()
        for node in root.iter() if node.tag.rsplit('}', 1)[-1] in wanted
    }


def generate(repo: Path, reference: Path) -> dict[str, bytes]:
    tracked = sorted(set(
        item.decode('utf-8') for item in git(repo, 'ls-files', '-z', '--', 'src/common').split(b'\0') if item
    ))
    rows, unmatched, baseline = [], [], {}
    counts = dict.fromkeys(CATEGORIES, 0)
    missing_working_tree = []
    for relative in tracked:
        kit_path, reference_path = repo / relative, reference / relative
        if not kit_path.is_file():
            missing_working_tree.append(relative)
            continue
        kit_bytes = kit_path.read_bytes()
        if not reference_path.is_file():
            unmatched.append({
                'path': relative, 'classification': 'no-same-path-reference',
                'review': 'check-renamed-counterpart-or-local-addition',
                'kit_sha256': digest(kit_bytes),
            })
            continue
        reference_bytes = reference_path.read_bytes()
        baseline[relative] = digest(reference_bytes)
        kit_text, reference_text = normalize(kit_bytes), normalize(reference_bytes)
        if kit_bytes == reference_bytes:
            category = 'byte-equal'
        elif kit_text is None or reference_text is None:
            category = 'binary-diff'
        elif kit_text == reference_text:
            category = 'normalized-equal'
        elif kit_text == brand_normalize(reference_text):
            category = 'brand-only'
        else:
            category = 'content-diff'
        counts[category] += 1
        added, removed, upstream_preview, kit_preview = (
            diff_stats(reference_text, kit_text)
            if reference_text is not None and kit_text is not None else (0, 0, '', '')
        )
        rows.append({
            'path': relative, 'classification': category,
            'review': 'review-required' if category in ('content-diff', 'binary-diff') else 'no-content-merge-proposed',
            'added_lines': added, 'removed_lines': removed,
            'kit_sha256': digest(kit_bytes), 'reference_sha256': digest(reference_bytes),
            'first_reference_change': upstream_preview, 'first_kit_change': kit_preview,
        })

    missing_core_paths = []
    for relative in CORE_REFERENCE_PATHS:
        path = reference / relative
        if path.is_file():
            baseline[relative] = digest(path.read_bytes())
        else:
            missing_core_paths.append(relative)
    manifest = ''.join(f'{sha}  {relative}\n' for relative, sha in sorted(baseline.items())).encode('utf-8')
    table = tsv([
        'path', 'classification', 'review', 'added_lines', 'removed_lines',
        'kit_sha256', 'reference_sha256', 'first_reference_change', 'first_kit_change',
    ], rows)
    unmatched_table = tsv(['path', 'classification', 'review', 'kit_sha256'], unmatched)
    own_git = None
    if (reference / '.git').exists():
        actual_root = Path(git(reference, 'rev-parse', '--show-toplevel').decode().strip()).resolve()
        if actual_root == reference:
            own_git = git(reference, 'rev-parse', 'HEAD').decode().strip()
    summary = {
        'schema_version': 1,
        'scope': 'git-tracked src/common working-tree files with the exact same relative path in the reference',
        'kit_head': git(repo, 'rev-parse', 'HEAD').decode().strip(),
        'kit_version': version_fields(repo / 'src/Version.props'),
        'reference_version': version_fields(reference / 'src/Version.props'),
        'reference_own_git_commit': own_git,
        'normalization': ['UTF-8 decode with optional BOM removal', 'CRLF and standalone CR to LF', 'no whitespace/comment/case/identifier normalization otherwise'],
        'brand_map_in_order': list(BRAND_MAP),
        'brand_map_scope': 'literal replacements on normalized reference text only; not a semantic equivalence claim',
        'diff_line_counts': 'normalized text before brand replacements; SequenceMatcher opcodes; not semantic change counts',
        'tracked_common_files': len(tracked),
        'compared_same_path_files': len(rows),
        'counts': counts,
        'no_same_path_reference_files': len(unmatched),
        'tracked_files_missing_from_working_tree': missing_working_tree,
        'reference_manifest_files': len(baseline),
        'missing_reference_core_paths': missing_core_paths,
        'reference_manifest_sha256': digest(manifest),
        'ledger_tsv_sha256': digest(table),
        'unmatched_tsv_sha256': digest(unmatched_table),
    }
    return {
        'common-drift-ledger.tsv': table,
        'common-drift-unmatched.tsv': unmatched_table,
        'upstream-baseline.sha256': manifest,
        'common-drift-summary.json': (json.dumps(summary, ensure_ascii=False, indent=2) + '\n').encode('utf-8'),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo-root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--reference-root', type=Path)
    parser.add_argument('--output-dir', type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--write', action='store_true', help='write the four report files only')
    mode.add_argument('--check', action='store_true', help='read-only check that report bytes match the current inputs')
    args = parser.parse_args()
    repo = args.repo_root.resolve()
    reference = (args.reference_root or repo / 'Source/PowerToys-main').resolve()
    output = (args.output_dir or repo / 'doc/devdoc/common-drift').resolve()
    if not (reference / 'src/common').is_dir():
        parser.error(f'Missing reference common directory: {reference / "src/common"}')
    if output.is_relative_to(repo / 'src') or output.is_relative_to(reference):
        parser.error('Reports must not be written inside Kit src or the reference snapshot.')
    reports = generate(repo, reference)
    if args.write:
        output.mkdir(parents=True, exist_ok=True)
        for name, data in reports.items():
            target = output / name
            if target.is_symlink():
                parser.error(f'Refusing to replace a report symlink: {target}')
            target.write_bytes(data)
    elif args.check:
        stale = [name for name, data in reports.items() if not (output / name).is_file() or (output / name).read_bytes() != data]
        if stale:
            print('Reports differ from current inputs: ' + ', '.join(stale), file=sys.stderr)
            return 1
    print(reports['common-drift-summary.json'].decode('utf-8'), end='')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
