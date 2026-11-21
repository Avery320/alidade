# Alidade

Alidade 是用來在 Rhino／Grasshopper 與 Torquetum Asterism 之間交換幾何和資料樹的 Grasshopper 外掛。

Alidade is a Grasshopper plugin for exchanging geometry and data trees with Torquetum Asterism.

## Structure

```text
.
├── README.md                                # 建置、安裝和元件使用說明
├── LICENSE                                  # MIT 授權
├── src/
│   └── Alidade/
│       ├── Alidade.csproj                   # 外掛組件與 SDK 參考設定
│       ├── AlidadeAssemblyInfo.cs           # 外掛名稱、識別碼與作者資訊
│       ├── ConnectToTorquetum.cs            # 以配對碼建立或關閉 Project 連線
│       ├── SendGeometry.cs                  # 送出完整幾何樹並追蹤寫入回執
│       ├── ReadGeometry.cs                  # 讀取 Geometry Output 的完成結果
│       ├── GeometryCollectionComponents.cs  # Build 與 Unpack 元件
│       ├── AlidadeAsyncComponent.cs         # 共用非同步請求與文件生命週期
│       ├── AlidadeClient.cs                 # Tor API 配對、Project 狀態及 HTTP 錯誤
│       ├── AlidadeClient.Geometry.cs        # 幾何收發、修訂比對和回執
│       ├── ProjectSession.cs                # 文件層級連線和 Project 型別
│       ├── AlidadeCollection.cs             # Grasshopper Collection 型別與持久化
│       ├── GeometryCollection.cs            # 幾何資料樹的格式驗證
│       ├── GeometryCollectionConverter.cs   # Rhino 幾何、資料樹和長度單位換算
│       ├── RhinoStepGeometry.cs             # Rhino STEP 資料的讀寫與檔頭處理
│       ├── ComponentIcons.cs                # 元件圖示載入
│       └── Icons/                           # 嵌入 GHA 的 PNG 與對應的 SVG 原稿
└── tests/
    └── Alidade.Checks/
        ├── Alidade.Checks.csproj            # 命令列驗收專案
        └── Program.cs                       # 格式、單位、資料樹和本機收發驗收
```

## Components

所有元件都在 Grasshopper 的 **Alidade** 分頁。

| 分組 | 元件 | 輸入 | 輸出 | 功能 |
| --- | --- | --- | --- | --- |
| Session | [Connect to Torquetum](src/Alidade/ConnectToTorquetum.cs) | Pairing Code、Connect | Project、Status | 以單次配對碼建立目前 Grasshopper 文件的連線。 |
| Convert | [Build Geometry Collection](src/Alidade/GeometryCollectionComponents.cs) | Geometry、Source Units | Collection、Diagnostics | 將 Rhino 幾何與分支轉成以公尺為單位的交換資料。 |
| Convert | [Unpack Geometry Collection](src/Alidade/GeometryCollectionComponents.cs) | Collection、Target Units | Geometry、Diagnostics | 將交換資料轉回 Rhino 幾何，保留分支與資料位置。 |
| Exchange | [Send Geometry](src/Alidade/SendGeometry.cs) | Project、Collection、ID、Send | Status | 送出整棵幾何樹，替換相同 ID 的 Geometry Input 資料。 |
| Exchange | [Read Geometry](src/Alidade/ReadGeometry.cs) | Project、ID | Collection、Status | 從同一 ID 的 Asterism Geometry Output 讀取最近一次完成的結果。 |

## 建置與安裝

使用能建置 `net7.0` 的 .NET SDK。專案建置時透過 NuGet 取得固定版本的 Rhino 8.6 SDK，執行時由 Rhino 提供 Grasshopper 與 RhinoCommon。執行外掛需要 Rhino 8 的 .NET Core 模式。

```sh
git clone https://github.com/Avery320/alidade.git
cd alidade
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

將 `src/Alidade/bin/Release/net7.0/Alidade.gha` 放到 Grasshopper 的第三方元件目錄，或執行 Rhino 的 `GrasshopperDeveloperSettings` 加入建置目錄。重啟 Rhino 並開啟 Grasshopper 後，就能從 **Alidade** 分頁加入元件。[McNeel 的 Grasshopper 元件目錄說明](https://developer.rhino3d.com/api/gh1/type/grasshopper.kernel.gh-pluginfoldertype/)

## 幾何收發

Rhino 與提供 **Tor API V0.0** 的 Torquetum Desktop 或本機 Web 須在同一台電腦執行。獨立 Project 可讀寫，LAN 共享 Project 完成同步後可讀取。

1. 在 Torquetum 的 **Project Settings → External Connections** 按 **Copy**，將單次配對碼貼到 Grasshopper Panel。把 Panel 接到 **Connect to Torquetum** 的 **Pairing Code**，將 **Connect** 切成 `True`。用 **Project** 輸出連接收發元件。
2. 開啟 Torquetum Studio。在 Asterism 放置 **Geometry Input**，將 **ID** 設為 `fixture`。把 Rhino 幾何接到 **Build Geometry Collection**，再把 **Collection** 接到 **Send Geometry**，將 **ID** 設成同樣的 `fixture`。用 Grasshopper Button 對 **Send** 發出一次觸發，也可在元件右鍵選 **Send**。
3. 把 **Geometry Input** 接到 **Geometry Output**，將 Output 的 **ID** 也設為 `fixture`。在 **Read Geometry** 填入相同 ID 並右鍵選 **Read**，再接 **Unpack Geometry Collection**。要將幾何存入 Station，可在 Asterism 執行 **Bake**。

Build 與 Unpack 可離線操作。省略單位腳位時，依 Grasshopper 定義所關聯的 Rhino 文件模型單位換算。資料樹可混合點、線、折線、平面、盒、三角網格，以及可表示為 STEP 的單一連通面、殼或實體。交換會保留分支路徑、分支內順序、空分支和 null 槽。

送出待確認時可用 **Check result** 查回執，或用 **Retry same request** 重送同一筆。**Connect** 切成 `False` 會取消本地等待並清除連線，Torquetum 已受理的寫入仍可完成。再次配對時使用新碼。

## 本機驗收

Tor API V0.0 的 HTTP 契約、Schema 和驗收樣本由 Torquetum 的 `contracts/tor-api/` 提供。準備與目前 Alidade 搭配的 Torquetum checkout。下方假設兩個儲存庫相鄰，並使用 macOS 上 Rhino 8 隨附的 .NET 執行檔。[Torquetum 整合 PR](https://github.com/Avery320/torquetum/pull/26)

```sh
dotnet build tests/Alidade.Checks/Alidade.Checks.csproj -c Release --warnaserror
alidade_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
tor_api_fixtures="../torquetum/contracts/tor-api/fixtures"
"$alidade_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Alidade.Checks/bin/Release/net7.0/Alidade.Checks.dll \
  src/Alidade/bin/Release/net7.0/Alidade.gha "$tor_api_fixtures/geometry-collection-basic.json"
```

驗收程式會從同目錄讀取 `geometry-collection.json`，檢查 V0.0 格式、Grasshopper SDK 的資料樹、單位往返及 STEP 檔頭。需要驗收 HTTP 時，先將 ID 皆為 `fixture` 的 Geometry Input 與 Geometry Output 直接相連，再於執行指令最後加上 `--exchange`，從標準輸入貼上獨立 Project 的配對碼並按 Enter。這個檢查涵蓋配對、寫入回執、同 ID 更新為 1493 mm 後讀回，以及混合 STEP 形狀的交換。Grasshopper 畫布操作和原生 Rhino STEP 檔案轉換可在 Rhino 視窗中驗收。

## 授權

本專案採用 [MIT License](LICENSE)。
