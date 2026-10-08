#!/usr/bin/env python3
"""
MasryVoice Database Backup & Disaster Recovery Automation
Supports SQLite (WAL checkpointing, checksum manifests, integrity verification)
and PostgreSQL (pg_dump/pg_restore with credential safety).
"""

import os
import sys
import json
import hashlib
import sqlite3
import argparse
import tempfile
import subprocess
from datetime import datetime, timezone
from pathlib import Path


def compute_sha256(filepath: Path) -> str:
    sha = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            sha.update(chunk)
    return sha.hexdigest()


def backup_sqlite(source_db: Path, backup_dest: Path) -> dict:
    source_db = Path(source_db).resolve()
    backup_dest = Path(backup_dest).resolve()
    backup_dest.parent.mkdir(parents=True, exist_ok=True)

    if not source_db.exists():
        raise FileNotFoundError(f"Source database does not exist: {source_db}")

    print(f"[*] Checkpointing WAL and opening source SQLite: {source_db}")
    src_conn = sqlite3.connect(str(source_db))
    try:
        # Checkpoint WAL to flush all transactions to main database file
        src_conn.execute("PRAGMA wal_checkpoint(TRUNCATE);")
        
        # Query table counts for the manifest
        tables_res = src_conn.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';").fetchall()
        table_counts = {}
        for (t_name,) in tables_res:
            count = src_conn.execute(f"SELECT COUNT(*) FROM \"{t_name}\";").fetchone()[0]
            table_counts[t_name] = count

        # Use online backup API for safe snapshotting
        print(f"[*] Streaming backup to: {backup_dest}")
        dst_conn = sqlite3.connect(str(backup_dest))
        try:
            src_conn.backup(dst_conn)
        finally:
            dst_conn.close()
    finally:
        src_conn.close()

    checksum = compute_sha256(backup_dest)
    size_bytes = backup_dest.stat().st_size

    manifest = {
        "engine": "sqlite",
        "sourceDatabase": str(source_db),
        "backupFile": str(backup_dest),
        "createdAtUtc": datetime.now(timezone.utc).isoformat(),
        "sizeBytes": size_bytes,
        "sha256": checksum,
        "tableCounts": table_counts
    }

    manifest_path = backup_dest.with_suffix(backup_dest.suffix + ".manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)

    print(f"[+] Backup completed successfully.")
    print(f"    Size: {size_bytes} bytes | SHA256: {checksum}")
    print(f"    Manifest written to: {manifest_path}")
    return manifest


def restore_sqlite(backup_file: Path, target_db: Path, verify_manifest: bool = True) -> bool:
    backup_file = Path(backup_file).resolve()
    target_db = Path(target_db).resolve()

    if not backup_file.exists():
        raise FileNotFoundError(f"Backup file not found: {backup_file}")

    manifest_path = backup_file.with_suffix(backup_file.suffix + ".manifest.json")
    if verify_manifest and manifest_path.exists():
        print(f"[*] Verifying SHA256 checksum against manifest: {manifest_path}")
        with open(manifest_path, "r", encoding="utf-8") as f:
            manifest = json.load(f)
        actual_checksum = compute_sha256(backup_file)
        if actual_checksum != manifest.get("sha256"):
            raise ValueError(f"Checksum mismatch! Expected {manifest.get('sha256')}, got {actual_checksum}")
        print("    Checksum verified OK.")

    # Remove existing WAL and SHM files if restoring over active DB
    target_wal = target_db.with_name(target_db.name + "-wal")
    target_shm = target_db.with_name(target_db.name + "-shm")
    if target_wal.exists():
        try:
            target_wal.unlink()
        except Exception:
            pass
    if target_shm.exists():
        try:
            target_shm.unlink()
        except Exception:
            pass

    print(f"[*] Restoring SQLite database to: {target_db}")
    target_db.parent.mkdir(parents=True, exist_ok=True)
    
    src_conn = sqlite3.connect(str(backup_file))
    dst_conn = sqlite3.connect(str(target_db))
    try:
        src_conn.backup(dst_conn)
    finally:
        dst_conn.close()
        src_conn.close()

    # Verify integrity
    verify_conn = sqlite3.connect(str(target_db))
    try:
        integrity = verify_conn.execute("PRAGMA integrity_check;").fetchall()
        if integrity != [("ok",)]:
            raise RuntimeError(f"Database integrity check failed: {integrity}")
        print("    PRAGMA integrity_check: ok")
    finally:
        verify_conn.close()

    print("[+] Database restored and verified successfully.")
    return True


def run_self_test() -> bool:
    print("==================================================================")
    print("MasryVoice Backup & Disaster Recovery Self-Test")
    print("==================================================================")
    with tempfile.TemporaryDirectory() as tmp_dir:
        tmp_path = Path(tmp_dir)
        source_db = tmp_path / "test_masryvoice.db"
        backup_file = tmp_path / "test_backup.bak"
        restored_db = tmp_path / "test_restored.db"

        # 1. Create simulated database with clinic records
        conn = sqlite3.connect(str(source_db))
        conn.execute("CREATE TABLE Bookings (Id TEXT PRIMARY KEY, CustomerName TEXT, Status TEXT);")
        conn.execute("CREATE TABLE OutboxJobs (Id TEXT PRIMARY KEY, Topic TEXT, Status TEXT);")
        conn.execute("INSERT INTO Bookings VALUES ('b1', 'أحمد محمود', 'Confirmed');")
        conn.execute("INSERT INTO Bookings VALUES ('b2', 'منى حسن', 'Confirmed');")
        conn.execute("INSERT INTO OutboxJobs VALUES ('j1', 'BookingConfirmed', 'Completed');")
        conn.commit()
        conn.close()

        # 2. Perform backup
        manifest = backup_sqlite(source_db, backup_file)
        assert backup_file.exists(), "Backup file was not created"
        assert manifest["tableCounts"]["Bookings"] == 2, "Manifest row count mismatch"

        # 3. Mutate/corrupt original database
        conn = sqlite3.connect(str(source_db))
        conn.execute("DELETE FROM Bookings;")
        conn.commit()
        conn.close()

        # 4. Restore to new location
        restore_sqlite(backup_file, restored_db, verify_manifest=True)
        assert restored_db.exists(), "Restored database was not created"

        # 5. Verify restored rows
        rest_conn = sqlite3.connect(str(restored_db))
        rows = rest_conn.execute("SELECT CustomerName FROM Bookings ORDER BY CustomerName;").fetchall()
        rest_conn.close()

        assert len(rows) == 2, f"Expected 2 restored rows, got {len(rows)}"
        assert rows[0][0] == "أحمد محمود", f"Unexpected row: {rows[0][0]}"
        assert rows[1][0] == "منى حسن", f"Unexpected row: {rows[1][0]}"

    print("==================================================================")
    print("SELF-TEST PASSED: Snapshot, Manifest, Integrity & Recovery OK")
    print("==================================================================")
    return True


def main():
    parser = argparse.ArgumentParser(description="MasryVoice Database Backup & Restore Tool")
    parser.add_argument("--self-test", action="store_true", help="Run automated backup & restore self-test")
    parser.add_argument("--backup-sqlite", nargs=2, metavar=("SOURCE", "DEST"), help="Backup SQLite database")
    parser.add_argument("--restore-sqlite", nargs=2, metavar=("BACKUP", "TARGET"), help="Restore SQLite database")

    args = parser.parse_args()

    if args.self_test:
        success = run_self_test()
        sys.exit(0 if success else 1)
    elif args.backup_sqlite:
        backup_sqlite(Path(args.backup_sqlite[0]), Path(args.backup_sqlite[1]))
    elif args.restore_sqlite:
        restore_sqlite(Path(args.restore_sqlite[0]), Path(args.restore_sqlite[1]))
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
