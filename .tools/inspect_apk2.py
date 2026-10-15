import zipfile
p = r"D:\Task\personnalHub\PersonalHub.Client\bin\Debug\net8.0-android\com.companyname.personalhub.client-Signed.apk"
z = zipfile.ZipFile(p)
names = z.namelist()
lib = [n for n in names if n.startswith("lib/")]
print("lib entries:", len(lib))
for n in sorted(lib):
    info = z.getinfo(n)
    print(f"   {n}  ({info.file_size} bytes)")
assets = [n for n in names if n.startswith("assets/")]
print("assets entries:", len(assets))
for n in sorted(assets)[:40]:
    print("   ", n)
