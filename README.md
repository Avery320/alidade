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

## 本機建置

從儲存庫根目錄使用 .NET SDK 7 以上版本執行下列指令。NuGet 會取得固定版本的 Rhino 8.6 SDK，執行外掛時由 Rhino 提供 Grasshopper 和 RhinoCommon。

```sh
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

建置產物位於 `src/Alidade/bin/Release/net7.0/Alidade.gha`，目標執行環境為 Rhino 8（.NET Core 模式）。

## 本機驗收

在 macOS 可使用已安裝的 Rhino 8.6 與其 .NET 7 執行環境，檢查 v1 契約和實際 Grasshopper SDK 的部分資料樹往返：

```sh
dotnet build src/Alidade/Alidade.csproj -c Release --warnaserror
dotnet build tests/Alidade.Checks/Alidade.Checks.csproj -c Release --warnaserror
alidade_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
"$alidade_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Alidade.Checks/bin/Release/net7.0/Alidade.Checks.dll \
  src/Alidade/bin/Release/net7.0/Alidade.gha contracts/alidade/v1/fixtures/geometry-collection.json
```

Intel Mac 使用 Rhino 目錄中的 `x86_64` 執行環境，Apple Silicon 使用 `arm64`。驗收程式會直接載入外掛組件。需由 Rhino 載入原生程式庫的幾何及元件接線，仍需用 Rhino 內的 Grasshopper 驗收。

## 交換契約

`contracts/alidade/v1` 包含 Alidade v1 的 [JSON Schema](contracts/alidade/v1/geometry-collection.schema.json) 與[合成測試資料](contracts/alidade/v1/fixtures/geometry-collection.json)，可在本機獨立使用。這兩個檔案原樣取自 Torquetum 的契約提交 `5a425a104dc6ba5978412d23e962999027f6cf11`。更新時先修改來源，再同步同版測試資料。

[連線契約](contracts/alidade/v1/connection.md)與[合成請求及回覆](contracts/alidade/v1/fixtures/connection.json)原樣取自 Torquetum 提交 `fe6420040d0facc2eb2b577365a25ccfad836244`。

## 授權

本專案採用 [MIT License](LICENSE)。
