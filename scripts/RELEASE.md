# 发版流程

> 2026-10-29 定的规矩：发布包由你自己打。这个文件只记「怎么打」和「装不上怎么判断」。

---

## 一、三条命令，按顺序跑

```bat
scripts\version-bump.bat patch        :: 版本号 +1，比如 1.0.4 → 1.0.5
scripts\client-android-release.bat    :: 打包 + 拷到 out\android + 发布到服务端
scripts\install-apk.bat               :: USB 连着手机时：先备份，再安全覆盖安装
```

每个 `.bat` 都可以双击，也可以在当前目录开 cmd 敲。

---

## 二、每一步到底干了什么

### 1. `version-bump.bat`

```
version-bump.bat          看当前版本
version-bump.bat patch    1.0.4 → 1.0.5      ← 平时用这个
version-bump.bat minor    1.0.5 → 1.1.0
version-bump.bat major    1.1.0 → 2.0.0
version-bump.bat 1.2.3    直接指定
```

改的是仓库根目录 `version.txt`，`Directory.Build.props` 会自动算出 versionCode：

```
versionCode = 主 * 10000 + 次 * 100 + 修订
1.0.4 → 10004
```

**这一步不能省。** 版本号不涨，手机会拒装，而它给的提示常常是「签名冲突」——
这是最容易误判的一条（2026-10-29 就踩了：手机上已是 1.0.3，又打了个 1.0.3）。

### 2. `client-android-release.bat`

自动做四件事，不用管中间产物在哪：

1. `dotnet build -c Release` 打包
2. 只认**构建完成后新写出的** `*-Signed.apk`，拿旧包会直接报错停下
3. 校验生成的 manifest 里真的有这次的 `versionCode`，对不上就失败
4. 复制成 `out\android\PersonalHub-<版本>-android.apk`，再发布到服务端的下载目录

可选参数：

```
client-android-release.bat aot        开 AOT，冷启动快一点，包大 ~7MB
client-android-release.bat 1.4.2      这一次强制用 1.4.2（不改 version.txt）
```

服务端没开也不影响出包，只是第 4 步的分发会报失败，包本身是好的。

### 3. `install-apk.bat`

```
install-apk.bat                  装 out\android 里最新的包
install-apk.bat D:\x\some.apk    装指定文件
```

它比「拷到手机点安装」安全：

- 先打印手机上现在的 versionName / versionCode
- 能读就先把数据库拉一份到 `backups\phone\`（Debug 包才允许，Release 会跳过并说明）
- 用 `adb install -r -d` 覆盖安装：**保留 app 私有目录，记录一条不丢**
- 失败时分会区分两种原因，并告诉你该干什么：
  - `INSTALL_FAILED_UPDATE_INCOMPATIBLE` → 真的是签名不同
  - `INSTALL_FAILED_VERSION_DOWNGRADE` → 版本号比手机上的低，去 `version-bump.bat patch`

---

## 三、手机端也可以不用 USB

`client-android-release.bat` 跑完会把包放到服务端，手机连同一个 Wi-Fi：

```
http://<这台电脑的 IP>:5001/app
```

在手机上打开这个地址下载安装 —— 同样是**覆盖安装**，不用卸载。
（服务端没开的话这一页打不开，改用 `install-apk.bat` 走 USB。）

**「卸载再装」永远不是更新方式。** 卸载会删掉 `/data/data/com.companyname.personalhub.client/files/personalhub.db3`，
连同里面所有还没同步到服务端的记录。

---

## 四、真遇到签名冲突怎么确认

别猜，两步验：

```bat
:: 手机上的包
adb shell pm path com.companyname.personalhub.client
adb pull <上一步的路径> phone.apk
keytool -printcert -jarfile phone.apk

:: 本地新包
keytool -printcert -jarfile out\android\PersonalHub-1.0.5-android.apk
```

比 SHA1：

- **一样** → 不是签名问题，回去看版本号
- **不一样** → 真的是两把钥匙，只能卸载一次。卸载前先备份（见下）

keytool 位置：

```
C:\Program Files\Android\jdk\jdk-8.0.302.8-hotspot\jdk8u302-b08\bin\keytool.exe
```

---

## 五、装前备份 / 装后救回

```bat
scripts\phone-db.bat backup              备份手机本地库到 backups\phone\
scripts\phone-db.bat restore <文件>       恢复到手机
```

要求：手机接 USB，且当前装的是 **Debug 包**（Release 包不允许 `run-as`）。

主库和 `-wal` 一起拷。只拷 `personalhub.db3` 会丢掉上次 checkpoint 之后的写入。

Release 包备份不了，那就靠服务端兜底：装之前先打开 app 一次，确认列表里该同步的显示「已同步」。
万一丢数据，装好后进「汇总 → 数据与恢复」从服务端拉回来。

---

## 六、钥匙在哪，别弄丢

```
C:\Users\Administrator\AppData\Local\Xamarin\Mono for Android\debug.keystore
密码 android / 别名 androiddebugkey / 库密码 android
```

指纹：

```
SHA1: 1A:99:28:70:12:50:C9:F0:4C:9D:E5:A0:C2:7E:5D:C7:7C:8E:80:08:75:58:6B:7A:EE:72:BA:66:94:30:2B:0B
```

三条铁律：

1. **别删、别重建。** 丢了就自动生成一把新钥匙，签名变了，手机上那个包从此装不上任何新包，
   只能卸载 —— 而卸载就是数据全丢。全程无提示。
2. **换电脑要把它一起带走。**
3. **手动背一份到 U 盘 / 网盘。** 它被 `.gitignore` 排除，不在仓库里，丢了没人能救。

要正式发给别人装，跑 `scripts\create-keystore.bat` 生成正式签名。
**代价：签名会变，现有手机必须卸载一次。** 挑刚同步完、数据都在服务端的时候做。

---

## 七、装完核对

设置 → 应用 → PersonalHub，或在 cmd 里：

```bat
adb shell dumpsys package com.companyname.personalhub.client | findstr versionName
```
