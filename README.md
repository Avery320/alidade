# Alidade

Alidade 是 Torquetum 的 Grasshopper 外掛專案，目標是在 Rhino／Grasshopper 和 Torquetum 專案之間交換幾何與資料樹。

Alidade is a Grasshopper plugin project for exchanging geometry and data trees with Torquetum.

## 開發範圍

- 透過 TOR API 連線至指定的 Torquetum 專案。
- 將 Grasshopper 的幾何寫入 Torquetum，並從 Torquetum 讀回專案幾何。
- 保留資料樹的分支路徑與項目順序，讓兩邊的結果可以接入後續運算。

## 專案狀態

目前可建置 Grasshopper 外掛的基礎組件 `Alidade.gha`。連線、幾何交換與資料樹元件規劃中。

## 本機建置

從儲存庫根目錄使用 .NET SDK 7 以上版本執行下列指令。NuGet 會取得固定版本的 Rhino 8.6 SDK，執行外掛時由 Rhino 提供 Grasshopper 和 RhinoCommon。

```sh
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

建置產物位於 `src/Alidade/bin/Release/net7.0/Alidade.gha`，目標執行環境為 Rhino 8（.NET Core 模式）。

## 交換契約

`contracts/alidade/v1` 包含 Alidade v1 的 [JSON Schema](contracts/alidade/v1/geometry-collection.schema.json) 與[合成測試資料](contracts/alidade/v1/fixtures/geometry-collection.json)，可在本機獨立使用。這兩個檔案原樣取自 Torquetum 的契約提交 `5a425a104dc6ba5978412d23e962999027f6cf11`。更新時先修改來源，再同步同版測試資料。

## 授權

本專案採用 [MIT License](LICENSE)。
