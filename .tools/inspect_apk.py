import zipfile, sys
p = r"D:\Task\personnalHub\PersonalHub.Client\bin\Debug\net8.0-android\com.companyname.personalhub.client-Signed.apk"
z = zipfile.ZipFile(p)
names = z.namelist()
print("total entries:", len(names))
asm = [n for n in names if n.startswith("assemblies/")]
print("--- assemblies entries:", len(asm))
for n in asm[:80]:
    print("   ", n)
blob = [n for n in names if n.endswith(".blob") or "assemblies.blob" in n]
print("--- blob entries:", len(blob))
for n in blob[:20]:
    print("   ", n)
keys = ["PersonalHub.Client.dll", "Google.Protobuf.dll", "Grpc.Net.Client.dll",
        "Microsoft.Maui.Controls.dll", "sqlite-net.dll", "SQLitePCLRaw", "System.Private.CoreLib"]
print("--- key dll presence:")
for k in keys:
    hit = [n for n in names if k in n]
    print("   ", k, ":", len(hit), hit[:3])
# also list top-level dirs
tops = sorted(set(n.split("/")[0] for n in names))
print("--- top-level:", tops)
