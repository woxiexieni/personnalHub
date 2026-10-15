import zipfile, os, glob

apk = r"D:\Task\personnalHub\PersonalHub.Client\bin\Debug\net8.0-android\com.companyname.personalhub.client-Signed.apk"
size = os.path.getsize(apk)
print(f"APK: {os.path.basename(apk)}")
print(f"大小: {size/1024/1024:.1f} MB")

z = zipfile.ZipFile(apk)
names = z.namelist()

abis = sorted(set(n.split("/")[1] for n in names if n.startswith("lib/")))
print(f"ABI: {abis}")

asm = [n for n in names if n.startswith("assemblies/")]
print(f"assemblies 条目: {len(asm)}")
for n in asm[:12]:
    print("   ", n, f"({z.getinfo(n).file_size} B)")

# pdb 是否还在
pdb = [n for n in names if n.lower().endswith(".pdb")]
print(f".pdb 条目: {len(pdb)}")

# 关键程序集
for k in ["PersonalHub.Client.dll", "Grpc.Net.Client.dll", "Google.Protobuf.dll",
          "Microsoft.Maui.Controls.dll", "SQLite-net.dll", "assemblies.blob"]:
    hit = [n for n in names if k in n]
    print(f"  {k}: {hit[:2] if hit else '未找到(可能已进 blob)'}")
