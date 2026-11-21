# Alidade

Alidade 是 Torquetum 的 Grasshopper 外掛專案，目標是在 Rhino／Grasshopper 和 Torquetum 專案之間交換幾何與資料樹。

Alidade is a Grasshopper plugin project for exchanging geometry and data trees with Torquetum.

## 本機建置

從儲存庫根目錄使用 .NET SDK 7 以上版本執行下列指令。NuGet 會取得固定版本的 Rhino 8.6 SDK，執行外掛時由 Rhino 提供 Grasshopper 和 RhinoCommon。

```sh
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

建置產物位於 `src/Alidade/bin/Release/net7.0/Alidade.gha`，目標執行環境為 Rhino 8（.NET Core 模式）。

## 安裝和範例

將建置產物 `Alidade.gha` 複製到 Grasshopper 載入第三方元件的資料夾，或把建置目錄加入 Rhino 的 `GrasshopperDeveloperSettings`。重新啟動 Rhino，開啟 Grasshopper，從 **Alidade** 分類加入元件。Rhino 與 Torquetum Desktop 或本機 Web 需在同一台電腦執行。 [McNeel Grasshopper 組件目錄說明](https://developer.rhino3d.com/api/gh1/type/grasshopper.folders/)

1. 在 Asterism 放置 **Geometry Input**，將 **ID** 設為 `fixture`。
2. 在 Torquetum 的 **Project Settings → Alidade** 按 **Copy**。將自動顯示的配對碼貼入 Grasshopper Panel，接到 **Connect to Torquetum** 的 **Pairing Code**，再用 Boolean Toggle 將 **Connect** 切成 `True`。把 **Project** 接到收發元件。
3. 把 Rhino 幾何接到 **Build Geometry Collection**，再將 **Collection** 接到 **Send Geometry**。在 **ID** 腳位填入 `fixture` 或接文字 Panel。用 Grasshopper Button 觸發 **Send**，或在元件右鍵選 **Send**。同一 ID 的每筆有效發送會替換整棵資料樹。
4. 要讀回，在 Asterism 接上 **Geometry Output** 並設定它的 **ID**。在 **Read Geometry** 的 **ID** 填入相同文字，可沿用 `fixture`。在元件右鍵選 **Read** 取得最近一次完成的輸出，再把 **Collection** 接到 **Unpack Geometry Collection**。

點、線、折線、平面、盒、網格和可轉為 STEP 的單一連通面、殼或實體，可以在同一棵樹傳送。轉換可離線操作，省略長度單位時使用 Rhino 模型單位。

**Connect** 切成 `False` 會中止本地收發等待並清掉讀回資料。已由 Torquetum 受理的寫入仍可完成。配對碼只能使用一次，重新配對時取得新碼。送出待確認時用 **Check result** 或 **Retry same request**。要將幾何儲存到 Station，請在 Asterism 執行 Bake。

## 本機驗收

在 macOS 可使用已安裝的 Rhino 8 與其 .NET 7 執行環境，檢查交換格式、STEP 檔頭處理和 Grasshopper SDK 的資料樹往返。

```sh
dotnet build src/Alidade/Alidade.csproj -c Release --warnaserror
dotnet build tests/Alidade.Checks/Alidade.Checks.csproj -c Release --warnaserror
alidade_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
"$alidade_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Alidade.Checks/bin/Release/net7.0/Alidade.Checks.dll \
  src/Alidade/bin/Release/net7.0/Alidade.gha contracts/alidade/v2/fixtures/geometry-collection-basic.json
```

Intel Mac 使用 Rhino 目錄中的 `x86_64` 執行環境，Apple Silicon 使用 `arm64`。驗收程式會載入外掛組件並檢查託管的 Grasshopper 資料樹和共用契約。實際元件接線、Rhino STEP 匯入及匯出仍待 Rhino 視窗驗收。

同一驗收程式加上 `--exchange` 可驗本機 Torquetum API。將尚未收到資料的 **Geometry Input** 直接接到 **Geometry Output**，兩個節點的 **ID** 都填 `fixture`，再從標準輸入送入專案的讀寫邀請。驗收涵蓋首次傳送、相同請求重試、資料樹讀回、拒絕過期版本和 STEP 面、殼與實體。

## 交換契約

配對、交易與 JSON Schema 由 Torquetum 的 `contracts/alidade` 維護。 [Torquetum 交換契約](https://github.com/Avery320/torquetum/pull/26/files)

本機驗收使用同版的 [基礎幾何測試資料](contracts/alidade/v2/fixtures/geometry-collection-basic.json)與 [混合 STEP 測試資料](contracts/alidade/v2/fixtures/geometry-collection.json)。

## 授權

本專案採用 [MIT License](LICENSE)。
