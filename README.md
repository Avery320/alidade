# Alidade

Alidade 是 Torquetum 的 Grasshopper 外掛專案，目標是在 Rhino／Grasshopper 和 Torquetum 專案之間交換幾何與資料樹。

Alidade is a Grasshopper plugin project for exchanging geometry and data trees with Torquetum.

## 開發範圍

- 透過 TOR API 連線至指定的 Torquetum 專案。
- 將 Grasshopper 的幾何寫入 Torquetum，並從 Torquetum 讀回專案幾何。
- 保留資料樹的分支路徑與項目順序，讓兩邊的結果可以接入後續運算。

## 專案狀態

`Alidade.gha` 提供 **Connect to Torquetum** 元件。在已啟用 Alidade 的本機 Torquetum 專案複製配對邀請，再從元件選單選擇 **Pair from clipboard**。輸出顯示專案與讀寫權限。使用 **Refresh status** 查詢目前狀態，或用 **Disconnect** 清除該 Grasshopper 文件的連線。重開或複製定義後須重新配對。

用 **Build Geometry Collection** 將 Rhino 點、線、折線、平面、盒與網格整理成集合，用 **Unpack Geometry Collection** 取回原生幾何。兩個元件都保留資料樹路徑、空分支、null 槽與項目鍵。單位可在元件中指定，省略時讀取 Grasshopper 文件關聯的 Rhino 模型單位。此轉換可離線操作。

支援 v2 的 Project 可交換單一連通的 STEP 面、殼與實體。兩個轉換元件透過 Rhino STEP 工具處理，單獨的 NURBS 曲線仍會回報目前不可精確交換。

**Select Project Geometry** 可從元件選單選取 Station 幾何。**Read Geometry** 將它讀成 Collection，**Send Geometry** 的選單提供明確的 **Create** 和 **Replace**。若結果待確認，可查回執或重試原請求，Grasshopper 重算只更新輸入狀態。

## 本機建置

從儲存庫根目錄使用 .NET SDK 7 以上版本執行下列指令。NuGet 會取得固定版本的 Rhino 8.6 SDK，執行外掛時由 Rhino 提供 Grasshopper 和 RhinoCommon。

```sh
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

建置產物位於 `src/Alidade/bin/Release/net7.0/Alidade.gha`，目標執行環境為 Rhino 8（.NET Core 模式）。

## 本機驗收

在 macOS 可使用已安裝的 Rhino 8.6 與其 .NET 7 執行環境，檢查 v1、v2 契約、STEP 檔頭處理和實際 Grasshopper SDK 的部分資料樹往返：

```sh
dotnet build src/Alidade/Alidade.csproj -c Release --warnaserror
dotnet build tests/Alidade.Checks/Alidade.Checks.csproj -c Release --warnaserror
alidade_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
"$alidade_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Alidade.Checks/bin/Release/net7.0/Alidade.Checks.dll \
  src/Alidade/bin/Release/net7.0/Alidade.gha contracts/alidade/v1/fixtures/geometry-collection.json
```

Intel Mac 使用 Rhino 目錄中的 `x86_64` 執行環境，Apple Silicon 使用 `arm64`。驗收程式會直接載入外掛組件。需由 Rhino 載入原生程式庫的幾何及元件接線，仍需用 Rhino 內的 Grasshopper 驗收。

同一驗收程式加上 `--exchange` 可驗真實的本機 Torquetum API。在測試專案取得讀寫邀請，從標準輸入送入配對碼。此測試會在該專案建立並更新一個測試集合，並確認相同請求重試、資料樹讀回與過期版本拒絕。若已協商 v2，測試會另外送出合成的 STEP 面、殼與實體。

## 交換契約

`contracts/alidade/v1` 包含 Alidade v1 的 [JSON Schema](contracts/alidade/v1/geometry-collection.schema.json) 與[合成測試資料](contracts/alidade/v1/fixtures/geometry-collection.json)，可在本機獨立使用。這兩個檔案原樣取自 Torquetum 的契約提交 `5a425a104dc6ba5978412d23e962999027f6cf11`。更新時先修改來源，再同步同版測試資料。

[連線與交換契約](contracts/alidade/v1/connection.md)包含本機配對、交易與 v2 版本協商。[合成連線資料](contracts/alidade/v1/fixtures/connection.json)取自 `fe64200`。

[v2 幾何規格](contracts/alidade/v2/geometry-collection.md)、[Schema](contracts/alidade/v2/geometry-collection.schema.json) 與[合成 STEP 測試資料](contracts/alidade/v2/fixtures/geometry-collection.json)與 Torquetum 共用。

## 授權

本專案採用 [MIT License](LICENSE)。
