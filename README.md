# Alidade

Alidade 是 Torquetum 的 Grasshopper 外掛專案，目標是在 Rhino／Grasshopper 和 Torquetum 專案之間交換幾何與資料樹。

Alidade is a Grasshopper plugin project for exchanging geometry and data trees with Torquetum.

## 開發範圍

- 透過 TOR API 連線至指定的 Torquetum 專案。
- 將 Grasshopper 的幾何送進 Asterism 的圖輸入，並讀取圖輸出。
- 保留資料樹的分支路徑與項目順序，讓兩邊的結果可以接入後續運算。

## 專案狀態

`Alidade.gha` 提供 **Connect to Torquetum** 元件。在已啟用 Alidade 的本機 Torquetum 專案複製配對邀請，再從元件選單選擇 **Pair from clipboard**。輸出顯示專案與讀寫權限。使用 **Refresh status** 查詢目前狀態，或用 **Disconnect** 清除該 Grasshopper 文件的連線。重開或複製定義後須重新配對。

用 **Build Geometry Collection** 將 Rhino 點、線、折線、平面、盒與網格整理成集合，用 **Unpack Geometry Collection** 取回原生幾何。兩個元件都保留資料樹路徑、空分支、null 槽與項目鍵。單位可在元件中指定，省略時讀取 Grasshopper 文件關聯的 Rhino 模型單位。此轉換可離線操作。

支援 v2 的 Project 可交換單一連通的 STEP 面、殼與實體。兩個轉換元件透過 Rhino STEP 工具處理，單獨的 NURBS 曲線仍會回報目前不可精確交換。

**Select Asterism Port** 選取圖上的幾何輸入或輸出。**Send Geometry** 在執行選單中的 **Send** 時將 Collection 傳給所選輸入。**Read Geometry** 將所選圖輸出讀成 Collection。若傳送待確認，可查回執或重試原請求。Grasshopper 重算只更新傳送元件的輸入狀態。

## 本機建置

從儲存庫根目錄使用 .NET SDK 7 以上版本執行下列指令。NuGet 會取得固定版本的 Rhino 8.6 SDK，執行外掛時由 Rhino 提供 Grasshopper 和 RhinoCommon。

```sh
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

建置產物位於 `src/Alidade/bin/Release/net7.0/Alidade.gha`，目標執行環境為 Rhino 8（.NET Core 模式）。

## 安裝和範例

將建置產物 `Alidade.gha` 複製到 Grasshopper 載入第三方元件的資料夾，或把建置目錄加入 Rhino 的 `GrasshopperDeveloperSettings`。重新啟動 Rhino，開啟 Grasshopper，從 **Alidade** 分類加入元件。Rhino 與 Torquetum Desktop 或本機 Web 需在同一台電腦執行。 [McNeel Grasshopper 組件目錄說明](https://developer.rhino3d.com/api/gh1/type/grasshopper.folders/)

1. 在 Torquetum 開啟測試專案，在 Asterism 新增 **Geometry Input** 與 **Geometry Output**，並把前者右側的 `Geometry` 接到後者左側的 `Geometry`。可在節點編輯 `Name`，方便從 Grasshopper 選取。
2. 在 **Project Settings** 選擇 **Create read-write invitation** 並複製配對碼。在 **Connect to Torquetum** 元件選單執行 **Pair from clipboard**，將 `Project` 接到兩個 **Select Asterism Port**，以及 **Send Geometry** 與 **Read Geometry**。
3. 在公釐 Rhino 文件準備 `{0}` 的點 `(1000, 0, 0)` 和線段，接到 **Build Geometry Collection**，再將 `Collection` 接到 **Send Geometry**。在第一個 Select 的 **Asterism ports** 選擇圖輸入，將 `Port Ref` 接到 Send 的 `Target`，執行 **Send**，並在 Asterism 預覽幾何。
4. 在第二個 Select 選擇圖輸出，將 `Port Ref` 接到 **Read Geometry**，再將 `Collection` 接到 **Unpack Geometry Collection**。填入 `Target units = cm`，點的 X 座標應為 100，分支及槽位應與來源一致。修改來源點後再次執行 **Send**，並在 Read 選 **Refresh read** 取得新結果。
5. 若其他用戶端更新了輸入，在 Select 選 **Refresh list** 後再次送出。若回執未確定，使用 **Check result** 或 **Retry same request**。重新打開 Torquetum 專案後需重新配對並傳入資料。若要將圖中的結果保存成 Station 幾何，可在 Asterism 執行 Bake。

Build 和 Unpack 保留有效的空分支、null 槽與項目鍵。CAD 能力依配對的 `collectionVersions` 和 `kinds` 決定。可用兩份共用 JSON 測試資料確認交換格式。 [v1 基礎幾何](contracts/alidade/v1/fixtures/geometry-collection.json) · [v2 STEP 面、殼與實體](contracts/alidade/v2/fixtures/geometry-collection.json)

## 本機驗收

在 macOS 可使用已安裝的 Rhino 8.6 與其 .NET 7 執行環境，檢查 v1、v2 契約、STEP 檔頭處理和實際 Grasshopper SDK 的部分資料樹往返：

```sh
dotnet build src/Alidade/Alidade.csproj -c Release --warnaserror
dotnet build tests/Alidade.Checks/Alidade.Checks.csproj -c Release --warnaserror
alidade_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
"$alidade_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Alidade.Checks/bin/Release/net7.0/Alidade.Checks.dll \
  src/Alidade/bin/Release/net7.0/Alidade.gha contracts/alidade/v1/fixtures/geometry-collection.json
```

Intel Mac 使用 Rhino 目錄中的 `x86_64` 執行環境，Apple Silicon 使用 `arm64`。驗收程式會載入外掛組件並檢查託管的 Grasshopper 資料樹和共用契約。實際元件接線、Rhino STEP 匯入及匯出仍待 Rhino 視窗驗收。

同一驗收程式加上 `--exchange` 可驗真實的本機 Torquetum API。請準備尚未收到資料的測試專案，其中一個 **Geometry Input** 直接接到唯一的 **Geometry Output**，並從標準輸入送入該專案的讀寫邀請。驗收涵蓋首次傳送、相同請求重試、資料樹讀回和拒絕過期版本。若已協商 v2，也會傳送並讀回合成的 STEP 面、殼與實體。

## 交換契約

`contracts/alidade/v1` 包含 Alidade v1 的 [JSON Schema](contracts/alidade/v1/geometry-collection.schema.json) 與[合成測試資料](contracts/alidade/v1/fixtures/geometry-collection.json)，可在本機獨立使用。這兩個檔案原樣取自 Torquetum 的契約提交 `5a425a104dc6ba5978412d23e962999027f6cf11`。更新時先修改來源，再同步同版測試資料。

[連線與交換契約](contracts/alidade/v1/connection.md)包含本機配對、交易與 v2 版本協商。[合成連線資料](contracts/alidade/v1/fixtures/connection.json)取自 `fe64200`。

[v2 幾何規格](contracts/alidade/v2/geometry-collection.md)、[Schema](contracts/alidade/v2/geometry-collection.schema.json) 與[合成 STEP 測試資料](contracts/alidade/v2/fixtures/geometry-collection.json)與 Torquetum 共用。

## 授權

本專案採用 [MIT License](LICENSE)。
