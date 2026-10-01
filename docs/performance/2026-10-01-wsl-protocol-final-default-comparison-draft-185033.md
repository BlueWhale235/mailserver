# 最终默认配置短对照：protocol-final（阶段报告草稿）

protocol-final 实际 SQLite/MySQL **445 项全部通过，0 失败、0 跳过**，TRX 保存于原始证据。产物 SHA256：`1be69e2e04c85a91d65327f06e29bea85612d3a53cbfade4b066b9bad2cf9023`。本组与旧 safe-final 数据独立，两版本 PreparedInboundEnabled=false，活跃收信/投递 4、重 MIME 2；先预热、五轮交替，服务/数据库/协议客户端均在 192.168.2.8，Release + Portable PDB 从 WSL 构建。

## 短对照结果

|操作|每版本样本|基线P95(s)|最终P95(s)|P95变化|基线吞吐(/s)|最终吞吐(/s)|吞吐变化|
|---|---:|---:|---:|---:|---:|---:|---:|
|SMTP1|80|0.508462|0.207679|-59.16%|17.918|25.942|+44.78%|
|SMTP10|80|1.386464|0.673495|-51.42%|5.926|7.664|+29.33%|
|IMAP SEARCH|BODY definitelyabsent|5|0.316849|0.102827|-67.55%|3.299|10.353|+213.83%|
|IMAP SEARCH|BODY tailneedle|5|0.387224|0.132345|-65.82%|2.959|8.240|+178.44%|
|IMAP SEARCH|SUBJECT "definitelyabsent"|5|0.018824|0.019730|+4.82%|68.866|71.532|+3.87%|
|IMAP SEARCH|SUBJECT "resume"|5|0.056609|0.021874|-61.36%|19.180|72.336|+277.15%|
|IMAP FETCH|(BODY.PEEK[HEADER])|5|0.061293|0.020691|-66.24%|18.563|53.545|+188.45%|
|IMAP FETCH|(BODY.PEEK[])|5|0.136863|0.076507|-44.10%|7.927|15.842|+99.86%|
|IMAP FETCH|(ENVELOPE BODYSTRUCTURE)|5|0.379559|0.272932|-28.09%|2.750|4.084|+48.52%|

SMTP 每尺寸每版本均 80/80 成功，零拒收。五轮 1 MiB 与 10 MiB 每轮 P95 均改善，吞吐每轮均改善。SUBJECT 未命中合并 P95 增加 4.82%、吞吐提高 3.87%，未超过 5% 门槛；这里只测五次，最近秩 P95 即最大值，必须结合持久 80 请求对照判断。其他六个 SEARCH/FETCH 条件 P95 均改善。此处 FETCH 操作计时不含建立连接与认证。

本组为短协议对照，未运行运行时采集；native trace、内存采样窗口与这里的无采集吞吐分开报告。两版本数据集固定 6 封邮件，新收信进入负载邮箱。当前大邮箱、IDLE、外发、独立诊断与两种 10 分钟混合负载待完成，不能仅由短对照声明所有阶段或完整稳定性已通过。没有真正整机 1 GiB 环境。

## 可追溯原始数据

- `builds/performance/wsl-iteration-20261001/analysis-evidence/default-workloads.jsonl` 中只选 `wsl-default-{baseline|protocol-final}-c4-r1..5`。
- `analysis-default.py` 与 `analysis-default-metrics.json` 记录计算逻辑、逐轮结果及合并样本。
- `protocol-final.trx` 为实际 SQLite/MySQL 回归结果。
- 旧 safe-final 的 Prepared=true 容量矩阵与持久对照保持独立，不能混入此组。

## 最终默认配置持久 IMAP 五轮补充

两版本 Prepared=false，每个条件/并发每版本 80 请求，先建立连接、TLS、登录、SELECT 和预热，操作计时排除这些建立成本。全部返回 OK；命中条件逐次断言返回数量 4，未命中数量 0。原始文件没有记录完整 UID 集合，因此这里只确认命中数量，完整集合另由协议一致性窗口补充。

|并发|条件|基线P95(s)|最终P95(s)|P95变化|基线吞吐(/s)|最终吞吐(/s)|
|---:|---|---:|---:|---:|---:|---:|
|1|SUBJECT definitelyabsent|0.074009|0.021009|-71.61%|15.619|74.452|
|1|SUBJECT resume|0.069713|0.015374|-77.95%|17.083|87.979|
|1|BODY tailneedle|0.334596|0.117065|-65.01%|3.311|9.993|
|1|BODY definitelyabsent|0.317129|0.105466|-66.74%|3.955|10.747|
|2|SUBJECT definitelyabsent|0.065010|0.022142|-65.94%|34.589|125.292|
|2|SUBJECT resume|0.062850|0.016094|-74.39%|33.122|145.657|
|2|BODY tailneedle|0.256001|0.155317|-39.33%|8.483|14.453|
|2|BODY definitelyabsent|0.287748|0.149260|-48.13%|8.090|14.846|
|4|SUBJECT definitelyabsent|0.069322|0.020244|-70.80%|61.500|254.666|
|4|SUBJECT resume|0.068910|0.017640|-74.40%|61.577|307.158|
|4|BODY tailneedle|0.410104|0.252960|-38.32%|11.180|19.500|
|4|BODY definitelyabsent|0.388870|0.216273|-44.38%|11.413|21.994|
|8|SUBJECT definitelyabsent|0.101184|0.057981|-42.70%|97.723|211.488|
|8|SUBJECT resume|0.083700|0.035393|-57.71%|108.801|271.428|
|8|BODY tailneedle|0.821088|0.578710|-29.52%|11.974|22.939|
|8|BODY definitelyabsent|0.828019|0.535368|-35.34%|12.152|21.867|

所有 16 个组合的五轮 P95 均改善。SUBJECT 未命中并发 1 P95 改善 71.61%，并发 8 改善 42.70%；短对照五样本 +4.82% 不代表持续搜索稳定退化。BODY 尾部与未命中四个并发档 P95 均改善，其中并发 8 尾命中改善 29.52%。这些结果来自固定 6 封邮件，不能代替大邮箱不同正文的验收。

详细逐轮统计见 `analysis-default-persistent-metrics.json`，原始文件为 `analysis-evidence/wsl-default-persistent-*-parallel.jsonl`。

补充集合校验：最终默认 c4 短对照 workloads.jsonl 的 IMAP SEARCH 行实际保存 matches；两版本五轮 SUBJECT resume 和 BODY tailneedle 完整集合均为 UID 7 8 9 10，两个未命中条件均为空。这是短对照的逐次完整集合证据，与持久脚本只记录数量的证据区分。
