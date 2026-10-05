# 从源码运行 BoardTrace

以下命令在 **Windows x64 的全新开发副本**中执行，工作目录始终为仓库根目录。构建源码、启动界面和运行完整生产流程是三个步骤：源码不附带已训练模型、历史数据库或账号；当前没有公开的安装包和模型下载。

```powershell
git clone https://github.com/berlin6908/BoardTrace.git BoardTrace-demo
Set-Location BoardTrace-demo
```

## 1. 准备环境

| 依赖 | 要求与入口 |
| --- | --- |
| PowerShell、Git | 使用 PowerShell 7；Git、`curl.exe`、`tar.exe` 可从命令行调用 |
| .NET SDK | [global.json](../global.json) 固定为 **10.0.401**，禁止版本滚动；[安装脚本](../scripts/Install-Dotnet.ps1) 下载到项目 `.local/dotnet` |
| Node.js / npm | [Web 配置](../src/boardtrace-web/package.json) 要求 Node.js **≥24.12.0**；[CI](../.github/workflows/ci.yml) 使用 24.20.0，通过锁文件安装 npm 依赖 |
| Python / uv | Python **3.11**、`uv` 可从命令行调用；[安装脚本](../scripts/Install-Python.ps1) 创建 `.venv`，安装固定 Torch、TorchVision、ONNX 和模拟器依赖 |
| SQL Server LocalDB | [安装脚本](../scripts/Install-LocalDb.ps1) 安装 SQL Server 2019 Express LocalDB；初始化使用 `(localdb)\BoardTrace` 实例 |
| Visual C++ x64 运行时 | ONNX Runtime 的 [Windows 原生依赖要求](https://onnxruntime.ai/docs/install/#requirements)；C# 推理使用 CPU |

先安装 Node.js/npm、Python/uv 和 Visual C++ x64 运行时，再执行：

```powershell
./scripts/Install-Dotnet.ps1
./scripts/Install-LocalDb.ps1
./scripts/Install-Python.ps1
. ./scripts/Enter-Environment.ps1
```

LocalDB MSI 安装会弹出 UAC，需要管理员授权；系统级 Visual C++ 安装也需要相应权限。项目内的 SDK 解压和 Python 虚拟环境安装无需管理员运行 PowerShell。Python 脚本安装 CUDA 12.6 版 PyTorch；数据准备、模拟器和 C# CPU 推理不需要 GPU，训练可显式选择 `--device cpu`。

## 2. 构建和基础检查

```powershell
./scripts/Build-Web.ps1
dotnet restore BoardTrace.sln --locked-mode
dotnet build BoardTrace.sln -c Release --no-restore
```

[Build-Web.ps1](../scripts/Build-Web.ps1) 执行 `npm ci` 和生产构建；随后 Server 构建会将 Web 产物复制到自己的 `wwwroot`。这一阶段不需要训练模型。

需要运行与 CI 相同的基础测试时，先启动 LocalDB，再执行：

```powershell
./scripts/Initialize-Database.ps1
dotnet test BoardTrace.sln -c Release --no-build
.venv/Scripts/python.exe -m pytest tests/python -q
```

这些是源码测试，不代替完整 200 图方案验证或生产流程演练。

## 3. 获取回放数据

```powershell
./scripts/Get-Data.ps1
.venv/Scripts/python.exe training/prepare_data.py
```

[Get-Data.ps1](../scripts/Get-Data.ps1) 下载固定提交的 [DeepPCB](https://github.com/tangsanli5201/DeepPCB)。数据原作者限定研究用途；图像为经过对齐和预处理的 PCB 视野，含人工添加缺陷。

[prepare_data.py](../training/prepare_data.py) 重建 **800 训练 / 200 验证 / 500 测试**的清单，并输出本地数据审计。工位读取 [inputs/validation.jsonl](../training/manifests/inputs/validation.jsonl)，仅凭 `sampleId` 选择图像；真值清单只供训练及评估使用。

## 4. 初始化独立开发库

选择一个未使用的新库名，例如 `BoardTrace_Demo_01`。保留已有验收库、工位 SQLite 和模拟器状态。

[Initialize-Development.ps1](../scripts/Initialize-Development.ps1) 面向默认 `BoardTrace` 开发库。这里使用同一个 Server 初始化入口，通过环境变量指定独立命名库，**不传 reset 参数**：

```powershell
./scripts/Initialize-Database.ps1
$env:ConnectionStrings__BoardTrace = 'Server=(localdb)\BoardTrace;Database=BoardTrace_Demo_01;Integrated Security=true;TrustServerCertificate=true'
dotnet src/BoardTrace.Server/bin/Release/net10.0/BoardTrace.Server.dll --initialize-development true --development-accounts .local/development-accounts.json
```

随机密码生成在 `.local/development-accounts.json`；设备凭据生成在 `.local/stations/STATION-01.json` 和 `STATION-02.json`。这些文件留在本地。

| 账号 | 用途 |
| --- | --- |
| `process` | Web 工艺工程师：模型上传、方案验证发布、批次下发 |
| `quality` | Web 质量工程师：首件批准、复核、返工和关批 |
| `operator` | 工位操作员登录 |
| `station-01` / `station-02` | 工位自动读取的设备账号 |

初始化后的数据库只有 schema、角色和账号，**没有已发布方案**。

## 5. 启动中央和工位

在第一个 PowerShell 窗口，从仓库根目录执行。每次重新打开中央服务窗口，都要设置同一个库名：

```powershell
$env:ConnectionStrings__BoardTrace = 'Server=(localdb)\BoardTrace;Database=BoardTrace_Demo_01;Integrated Security=true;TrustServerCertificate=true'
./scripts/Start-Server.ps1 -SkipBuild
```

浏览器打开 <http://127.0.0.1:5180/>。另一个窗口可检查连接：

```powershell
Invoke-RestMethod http://127.0.0.1:5180/health
```

在第二个 PowerShell 窗口，从仓库根目录启动 WPF：

```powershell
./scripts/Start-Station.ps1 -Station STATION-01
```

工位默认使用 `data/stations/STATION-01.db`；STATION-02 使用自己的文件。新开发副本将创建自己的档案，之后重启须保留同一个 SQLite、方案缓存和设备凭据。关闭 WPF 或在中央窗口按 Ctrl+C 结束进程。

## 6. 准备并发布检测方案

完整六类检测需要符合项目输入契约的 ONNX 模型。可在本机训练导出，或使用已有的、符合契约的模型。当前仓库没有公开的训练权重下载；历史指标对应原实测模型，不承诺任意重新训练的模型得到相同结果。

仓库提供 [训练脚本](../training/train.py)、[配置](../training/train-config.json)、[模型定义](../training/model.py) 和 [ONNX 导出](../training/export.py)：

```powershell
. ./scripts/Enter-Environment.ps1
.venv/Scripts/python.exe training/train.py --output data/models/demo-training --device cpu
.venv/Scripts/python.exe training/export.py --checkpoint data/models/demo-training/best.pt --output data/models/demo-export --samples 4
```

有可用 NVIDIA GPU 时，可将训练参数改为 `--device cuda`。输出目录须为空；训练默认 30 轮，CPU 训练耗时较长。`best.pt` 由开发验证集选择，导出产物为 `detector.onnx` 与 `model.manifest.json`。导出的 4 图对照只检查转换一致性，正式发布仍需中央完整验证。

模型输入为 float32 `[1,3,640,640]`，三个通道依次是待检灰度、参考灰度、绝对差分，各除以 255；模型内部再执行 `(value - 0.5) / 0.5`。输出为 `boxes`、`labels`、`scores`；[评估代码](../training/detection_evaluation.py) 定义六类标签，[evaluation-config.json](../training/evaluation-config.json) 固定逐类阈值。

使用 `process` 登录 Web，在方案页上传 ONNX，创建“成对 ONNX 六类检测”草稿，逐类填写阈值和本次目标，执行固定 **200 图 validation** 的真实检测。完成并达到 [冻结发布门槛](../training/release-policy.json) 后发布；当前门槛为 Precision ≥90%、Recall ≥95%、检测 p95 ≤1500ms。本机性能影响是否通过。已发布版本固定模型、参考资产、参数和算法身份。

## 7. 运行两件 PLC 回放

先在 Web/WPF 完成以下操作：

1. `process` 用已发布方案创建批次，计划数量设为 2，分配 STATION-01。
2. `operator` 登录工位，刷新、下载并加载该批次。选择样本，可勾选“使用参考图构造正常输入”执行首件。
3. 等待首件上传，由 `quality` 在 Web 批准；工位刷新批准并执行“本班在线启动”。构造正常输入只演示流程，不代表真实良品误报率。
4. 工位保持“数据集回放”，将 PLC 地址设为 `127.0.0.1`、端口 `1502`，点击“连接 PLC”。

在第三个 PowerShell 窗口，从仓库根目录生成只含样本 ID 的输入并启动模拟器：

```powershell
New-Item -ItemType Directory -Force .local/demo | Out-Null
Get-Content training/manifests/inputs/validation.jsonl -TotalCount 2 |
    ForEach-Object { @{ sampleId = ($_ | ConvertFrom-Json).sampleId } | ConvertTo-Json -Compress } |
    Set-Content -Encoding ascii .local/demo/samples.jsonl

.venv/Scripts/python.exe -m tools.simulator --scenario normal --host 127.0.0.1 --port 1502 --count 2 --product-prefix DEMO-S1 --samples .local/demo/samples.jsonl --state .local/demo/plc-station-01.db --output .local/demo/plc-station-01.jsonl --timeout 30
```

`normal` 指正常握手场景；两件产品的 Pass/Fail 均由实际图像算法产生。模拟器不提供真值或预定结论。[模拟器](../tools/simulator/__main__.py) 的 `--count` 是该 SQLite 状态中包含历史结果的累计目标；首次演练使用新状态，恢复同一轮时保留原状态。第二工位须使用另一端口、产品前缀和状态文件。

在 Web 追溯页查看两件检测与图像，使用 `quality` 处理质量异常、按需开返工票并复检。生产件上传、设备 ACK 和质量处置完成后再关批；首件、生产和复检在报告中分别统计。

## 故障定位

- `/health` 失败：看中央前台日志，核对当前窗口的库名与 LocalDB 实例是否启动。
- Web 登录失败：使用该开发副本生成的人员账号密码；设备账号由工位凭据文件读取。
- 原生库加载失败：核对 Windows x64 和 Visual C++ x64 运行时。
- 无已发布方案：先执行本次真实 200 图验证发布；初始化不会导入旧发布事实。
- PLC 不接件：核对方案已下载、首件已批准、当前操作员已在线启动、批次未满量且没有待 ACK 结果。
- 验证未达标：查看本次模型、逐类阈值和实际报告；源码构建成功不表示模型或完整业务已经验证通过。
