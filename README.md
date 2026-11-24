# Nuncius

Nuncius 是用來在 Rhino／Grasshopper 與 Torquetum Asterism 之間交換幾何和資料樹的 Grasshopper 外掛。

Nuncius is a Grasshopper plugin for exchanging geometry and data trees with Torquetum Asterism.

## Structure

```text
.
├── README.md                                # 建置、安裝和元件使用說明
├── LICENSE                                  # MIT 授權
├── tor-api/                                 # Torquetum API submodule，提供共用 Schema 和樣本
├── src/
│   └── Nuncius/
│       ├── Nuncius.csproj                   # 外掛組件與 SDK 參考設定
│       ├── NuncAssemblyInfo.cs           # 外掛名稱、識別碼與作者資訊
│       ├── ConnectToTorquetum.cs            # 以配對碼建立或關閉 Project 連線
│       ├── SendGeometry.cs                  # 送出完整幾何樹並追蹤寫入回執
│       ├── ReadGeometry.cs                  # 讀取 Geometry Output 的完成結果
│       ├── GeometryCollectionComponents.cs  # Build 與 Unpack 元件
│       ├── NuncAsyncComponent.cs         # 共用非同步請求與文件生命週期
│       ├── NuncClient.cs                 # Tor API 配對、Project 狀態及 HTTP 錯誤
│       ├── NuncClient.Geometry.cs        # 幾何收發、修訂比對和回執
│       ├── ProjectSession.cs                # 文件層級連線和 Project 型別
│       ├── NuncCollection.cs             # Grasshopper Collection 型別與持久化
│       ├── GeometryCollection.cs            # 共用 Schema 的欄位規則與幾何驗證
│       ├── GeometryCollectionConverter.cs   # Rhino 幾何、資料樹和長度單位換算
│       ├── RhinoStepGeometry.cs             # Rhino STEP 資料的讀寫與檔頭處理
│       ├── ComponentIcons.cs                # 元件圖示載入
│       └── Icons/                           # 嵌入 GHA 的 PNG 與對應的 SVG 原稿
└── tests/
    └── Nuncius.Checks/
        ├── Nuncius.Checks.csproj            # 命令列驗收專案
        └── Program.cs                       # 格式、單位、資料樹和本機收發驗收
```

## Components

所有元件都在 Grasshopper 的 **Nuncius** 分頁。

| 分組 | 元件 | 輸入 | 輸出 | 功能 |
| --- | --- | --- | --- | --- |
| Session | [Connect to Torquetum](src/Nuncius/ConnectToTorquetum.cs) | Pairing Code、Connect | Project、Status | 以單次配對碼建立目前 Grasshopper 文件的連線。 |
| Convert | [Build Geometry Collection](src/Nuncius/GeometryCollectionComponents.cs) | Geometry、Source Units | Collection、Diagnostics | 將 Rhino 幾何與分支轉成以公尺為單位的交換資料。 |
| Convert | [Unpack Geometry Collection](src/Nuncius/GeometryCollectionComponents.cs) | Collection、Target Units | Geometry、Diagnostics | 將交換資料轉回 Rhino 幾何，保留分支與資料位置。 |
| Exchange | [Send Geometry](src/Nuncius/SendGeometry.cs) | Project、Collection、ID、Send | Status | 按 Send 將一棵幾何樹送到相同 ID，供 Asterism 讀取。 |
| Exchange | [Read Geometry](src/Nuncius/ReadGeometry.cs) | Project、ID、Read | Collection、Status | 按 Read 取得對應 Asterism Geometry Output 最近一次送出的結果。 |

## 建置與安裝

使用能建置 `net7.0` 的 .NET SDK。專案建置時透過 NuGet 取得固定版本的 Rhino 8.6 SDK，執行時由 Rhino 提供 Grasshopper 與 RhinoCommon。執行外掛需要 Rhino 8 的 .NET Core 模式。

```sh
git clone --recurse-submodules https://github.com/Avery320/nuncius.git
cd nuncius
dotnet build src/Nuncius/Nuncius.csproj --configuration Release
```

已有 checkout 時，先執行 `git submodule update --init`。建置會把固定版本的幾何 Schema 嵌入 `Nuncius.gha`，供離線欄位驗證使用。

將 `src/Nuncius/bin/Release/net7.0/Nuncius.gha` 放到 Grasshopper 的第三方元件目錄，或執行 Rhino 的 `GrasshopperDeveloperSettings` 加入建置目錄。重啟 Rhino 並開啟 Grasshopper 後，就能從 **Nuncius** 分頁加入元件。 [McNeel 的 Grasshopper 元件目錄說明](https://developer.rhino3d.com/api/gh1/type/grasshopper.kernel.gh-pluginfoldertype/)

由 Alidade 升級時，請以 `Nuncius.gha` 取代舊外掛，並在原本的 Connect to Torquetum 元件使用新配對碼配對一次。

## 幾何收發

Rhino 與提供 **Tor API V0.0** 的 Torquetum Desktop 或本機 Web 須在同一台電腦執行。獨立 Project 可讀寫，LAN 共享 Project 完成同步後可讀取。

1. 首次在 Torquetum 的 **Project Settings → External Connections** 按 **Copy**，將單次配對碼貼到 Grasshopper Panel。把 Panel 接到 **Connect to Torquetum** 的 **Pairing Code**，將 **Connect** 切成 `True`。用 **Project** 輸出連接收發元件，並儲存 Grasshopper 定義以保留同一個連線元件。
2. **Grasshopper → Asterism。** 把 Rhino 幾何接到 **Build Geometry Collection**，再將 **Collection** 接到 **Send Geometry**。在 **Send Geometry** 和 Asterism **Geometry Input** 填入相同的 **ID**。用 Grasshopper Button 觸發 **Send**，顯示 **Sent** 後，在 **Geometry Input** 按 **Read** 更新接線下游。
3. **Asterism → Grasshopper。** 將幾何接到 **Geometry Output**，並在 **Geometry Output** 與 **Read Geometry** 填入相同的 **ID**。在 Asterism 按 **Send**，再用 Grasshopper Button 觸發 **Read**。把讀回的 **Collection** 接到 **Unpack Geometry Collection** 還原成 Rhino 幾何。

要驗收往返，可將 **Geometry Input** 接到 **Geometry Output**。在 Asterism 要把幾何存入 Station 時，使用 **Bake**。

Nuncius 在目前電腦上記住這個元件的授權。重新開啟同一個 Torquetum Project 和 Grasshopper 定義後，**Connect** 開啟時會恢復連線。若 Grasshopper 先開啟，在 Torquetum 開好專案後重開 **Connect** 即可。重連密鑰存於目前使用者的本機資料，Grasshopper 定義保存用來識別連線的元件 ID。要撤銷時，在 Torquetum 的 **External Connections** 按該授權的 **Revoke**。

Build 與 Unpack 可離線操作。省略單位腳位時，依 Grasshopper 定義所關聯的 Rhino 文件模型單位換算。資料樹可混合點、線、折線、平面、盒、三角網格，以及可表示為 STEP 的單一連通面、殼或實體。交換會保留分支路徑、分支內順序、空分支和 null 槽。

兩個方向都以 ID 保留最近一次送出的一份資料。改動上游幾何後，依序按 **Send → Read** 更新接收端。**Connect** 切成 `False` 會取消本地等待並清除連線，Torquetum 已受理的寫入仍可完成。

## 本機驗收

下方使用 `tor-api/` submodule 中的 V0.0 樣本，以及 macOS 上 Rhino 8 隨附的 .NET 執行檔。 [Torquetum API](https://github.com/Avery320/torquetum-api)

```sh
dotnet build tests/Nuncius.Checks/Nuncius.Checks.csproj -c Release --warnaserror
nunc_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
"$nunc_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Nuncius.Checks/bin/Release/net7.0/Nuncius.Checks.dll \
  src/Nuncius/bin/Release/net7.0/Nuncius.gha tor-api/fixtures/geometry-collection-basic.json
```

驗收程式會從同目錄讀取 `geometry-collection.json`，檢查 V0.0 格式、資料樹、單位往返和 STEP。驗收 HTTP 時，將 ID 皆為 `fixture` 的 Geometry Input 與 Geometry Output 相連，執行指令時帶上 `--exchange` 並貼上獨立 Project 的配對碼。每次程式提示後，在 Asterism 依序按 **Read → Send**，再回到終端機按 Enter。驗收會檢查同 ID 更新為 1493 mm 與混合 STEP 形狀的讀回結果。

## 授權

本專案採用 [MIT License](LICENSE)。
