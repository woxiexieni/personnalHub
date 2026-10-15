import zipfile

p = r"D:\Task\personnalHub\PersonalHub.Client\bin\Release\net8.0-android\com.companyname.personalhub.client-Signed.apk"
z = zipfile.ZipFile(p)
names = z.namelist()
print("APK:", p)
print("总条目:", len(names))

# ABI 目录
abis = sorted({n.split("/")[1] for n in names if n.startswith("lib/") and len(n.split("/")) > 2})
print("ABI:", abis)

# assemblies 条目
asm = [n for n in names if n.startswith("assemblies/")]
print("assemblies/ 条目数:", len(asm))
for n in asm[:15]:
    print("   ", n, z.getinfo(n).file_size)

# blob
blob = [n for n in names if n.endswith(".blob")]
print("blob:", [(n, z.getinfo(n).file_size) for n in blob])

# 关键托管程序集（release 下可能是 .dll 或 .dll.so，也可能在 blob 里）
keys = ["PersonalHub.Client", "Grpc.Net.Client", "Google.Protobuf", "sqlite-net", "SQLitePCLRaw", "Microsoft.Maui.Controls"]
print("--- 关键名出现次数 ---")
for k in keys:
    hits = [n for n in names if k in n]
    print(f"   {k}: {len(hits)}")

# assets 里找 assemblies blob / 记录文件
assets = [n for n in names if n.startswith("assets/")]
print("--- assets 前 30 ---")
for n in assets[:30]:
    print("   ", n, z.getinfo(n).file_size)
