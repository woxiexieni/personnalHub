import sqlite3, os, glob

p = r"C:\Users\Administrator\AppData\Local\PersonalHub\personalhub.db3"
print("db3 大小:", os.path.getsize(p), "B")
for extra in glob.glob(p.replace(".db3", ".db3-*")) + glob.glob(p + "-*"):
    print("  附带文件:", os.path.basename(extra), os.path.getsize(extra), "B")

con = sqlite3.connect(p)
cur = con.cursor()
print("表:", [r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'")])
print("journal_mode:", cur.execute("PRAGMA journal_mode").fetchone()[0])
n = cur.execute("SELECT COUNT(*) FROM records").fetchone()[0]
print("records 行数:", n)
for row in cur.execute("SELECT id, amount, category_id, pay_method, note, spent_at FROM records ORDER BY id"):
    print("   ", row)
con.close()
