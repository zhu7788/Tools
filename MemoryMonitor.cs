using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

/// <summary>
/// 内存监控器 - 在屏幕上实时显示内存使用情况（包含系统物理内存 + 堆内存泄漏检测）
/// </summary>
public class MemoryMonitor : MonoBehaviour
{
    [Header("监控设置")]
    public float checkInterval = 1f; // 检测间隔（秒）
    public bool showGUI = true; // 是否显示屏幕UI
    public int maxHistoryCount = 60; // 历史记录数量（用于绘图）
    
    [Header("泄漏检测")]
    public long leakWarningMB = 50;   // 泄漏警告阈值（MB）- 超过此值提示可能有泄漏

    [Header("警告阈值 (MB)")]
    public long warningThresholdMB = 1500;
    public long dangerThresholdMB = 2500;

    private float lastCheckTime = 0f;
    private long currentMemoryMB = 0;
    private long peakMemoryMB = 0;
    
    // ========== 堆内存信息（关键：检测场景泄漏）==========
    private long monoHeapSizeMB = 0;           // Mono堆总大小（已分配给Unity的托管内存）
    private long monoUsedSizeMB = 0;            // Mono已用大小（实际使用的托管内存）
    private long managedHeapMB = 0;             // 托管堆大小（GC.GetTotalMemory）
    private long totalAllocatedMB = 0;          // Unity总分配内存（Profiler.GetTotalAllocatedMemory）
    private long reservedTotalMB = 0;           // Unity预留总内存
    
    // ========== 场景切换泄漏检测 ==========
    private MemorySnapshot snapshotBefore = null;      // 进场景前快照
    private MemorySnapshot snapshotAfter = null;       // 出场景后快照
    private string leakStatus = "等待测试...";          // 泄漏状态描述
    private Color leakStatusColor = Color.gray;
    
    // ========== 系统物理内存信息 ==========
    private long systemTotalMemoryMB = 0;      // 系统总物理内存
    private long systemUsedMemoryMB = 0;        // 系统已使用内存
    private long systemFreeMemoryMB = 0;        // 系统空闲内存
    private float systemUsagePercent = 0f;      // 系统内存使用率(%)
    
    // 进程私有工作集（本进程占用）
    private long processPrivateMemoryMB = 0;
    
    // 内存历史记录（用于绘制曲线图）
    private List<long> memoryHistory = new List<long>();
    
    // GUI样式
    private GUIStyle labelStyle;
    private GUIStyle boxStyle;
    private GUIStyle smallLabelStyle;
    private GUIStyle percentStyle;

    /// <summary>
    /// 内存快照类 - 用于对比进出场景的内存差异
    /// </summary>
    [Serializable]
    public class MemorySnapshot
    {
        public string name;                  // 快照名称（如"进入战斗场景"）
        public DateTime timestamp;            // 时间戳
        public long monoHeapMB;              // Mono堆大小
        public long monoUsedMB;              // Mono已用大小
        public long managedHeapMB;           // 托管堆大小
        public long totalAllocatedMB;        // 总分配内存
        public long processMemoryMB;         // 进程工作集
        
        public override string ToString()
        {
            return $"[{timestamp:HH:mm:ss}] 堆:{monoHeapMB}MB | 已用:{monoUsedMB}MB | 总分配:{totalAllocatedMB}MB | 进程:{processMemoryMB}MB";
        }
    }

    #region Windows API - 获取系统物理内存
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;              // 内存使用率 (0-100)
        public ulong ullTotalPhys;             // 物理内存总量 (字节)
        public ulong ullAvailPhys;             // 可用物理内存 (字节)
        public ulong ullTotalPageFile;         // 页文件总量
        public ulong ullAvailPageFile;         // 可用页文件
        public ulong ullTotalVirtual;         // 虚拟地址空间总量
        public ulong ullAvailVirtual;          // 可用虚拟地址空间
        public ulong ullAvailExtendedVirtual;  // 保留
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    #endregion

    void Start()
    {
#if !UNITY_EDITOR && DEVELOPER_BUILD
        // 非编辑器模式下，只有开发构建才默认显示
        this.showGUI = false;
#endif
    }

    void Update()
    {
        if (Time.realtimeSinceStartup - lastCheckTime >= checkInterval)
        {
            CheckMemory();
            lastCheckTime = Time.realtimeSinceStartup;
        }
    }

    void CheckMemory()
    {
        // ========== 1. 堆内存数据（关键：检测场景泄漏）==========
        // 托管堆（C#对象）
        managedHeapMB = System.GC.GetTotalMemory(false) / (1024 * 1024);
        
#if UNITY_2020_1_OR_NEWER
        // Mono/IL2CPP 堆大小
        monoHeapSizeMB = (long)(UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / (1024 * 1024));
        // Mono已用大小
        monoUsedSizeMB = (long)(UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024));
        // Unity总分配内存
        totalAllocatedMB = (long)(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024));
        // 预留总内存
        reservedTotalMB = (long)(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024 * 1024));
#endif
        
        // 使用Unity内存作为当前显示值
        currentMemoryMB = Math.Max(managedHeapMB, totalAllocatedMB);
        
        // ========== 2. 系统物理内存（Windows API）==========
        GetSystemMemoryInfo();
        
        // ========== 3. 进程工作集 ==========
#if UNITY_EDITOR || DEVELOPER_BUILD
        processPrivateMemoryMB = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
#endif
        
        // 更新峰值
        if (currentMemoryMB > peakMemoryMB)
        {
            peakMemoryMB = currentMemoryMB;
        }
        
        // 记录历史
        memoryHistory.Add(currentMemoryMB);
        if (memoryHistory.Count > maxHistoryCount)
        {
            memoryHistory.RemoveAt(0);
        }
        
        // 自动清理逻辑
        if (currentMemoryMB > dangerThresholdMB)
        {
            Debug.LogError($"🔴 内存严重超标: {currentMemoryMB} MB，触发紧急清理！");
            
            Resources.UnloadUnusedAssets();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            
            try
            {
                var resMgrType = System.Type.GetType("FW.ResMgr");
                if (resMgrType != null)
                {
                    var instProp = resMgrType.GetProperty("inst");
                    var resMgrInst = instProp.GetValue(null);
                    if (resMgrInst != null)
                    {
                        var unloadMethod = resMgrType.GetMethod("UnloadUnused");
                        unloadMethod.Invoke(resMgrInst, null);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"自动清理失败: {e.Message}");
            }
        }
        else if (currentMemoryMB > warningThresholdMB)
        {
            Debug.LogWarning($"⚠️ 内存占用过高: {currentMemoryMB} MB");
            
            Resources.UnloadUnusedAssets();
        }
    }

    /// <summary>
    /// 获取系统物理内存信息（Windows API）
    /// </summary>
    void GetSystemMemoryInfo()
    {
        try
        {
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                systemTotalMemoryMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                long availMemoryMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                systemUsedMemoryMB = systemTotalMemoryMB - availMemoryMB;
                systemFreeMemoryMB = availMemoryMB;
                systemUsagePercent = memStatus.dwMemoryLoad; // 0-100%
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"获取系统内存信息失败: {e.Message}");
        }
    }

    void OnGUI()
    {
        if (!showGUI) return;

        if (labelStyle == null) InitStyles();

        float padding = 10f;
        float width = 420f;
        float height = 600f;
        float x = Screen.width - width - padding;
        float y = 100f; // 下移到帧率下方，避免重叠

        // 半透明背景
        GUILayout.BeginArea(new Rect(x, y, width, height));
        GUI.color = new Color(0.6f, 1f, 0.7f); // 浅绿色标题
        GUI.Box(new Rect(0, 0, width, height), "📊 Memory Monitor", boxStyle);

        GUILayout.Space(28);

        // ========== 堆内存（关键：检测场景泄漏）==========
        GUILayout.Label("【堆内存 - 泄漏检测关键】", smallLabelStyle);
        
        // Mono堆大小（根据使用率变色）
        Color originalColor = GUI.color;
        if (monoUsedSizeMB > dangerThresholdMB)
        {
            GUI.color = Color.red;
        }
        else if (monoUsedSizeMB > warningThresholdMB)
        {
            GUI.color = new Color(1f, 0.6f, 0f); // 橙色
        }
        else
        {
            GUI.color = new Color(0.4f, 1f, 0.5f); // 绿色
        }

        GUILayout.Label($"▸ Mono已用: {monoUsedSizeMB} MB", labelStyle);
        
        //GUI.color = new Color(1f, 1f, 1f); // 白色
        GUILayout.Label($"▸ Mono堆大小: {monoHeapSizeMB} MB", labelStyle);
        GUILayout.Label($"▸ 托管堆(GC): {managedHeapMB} MB", labelStyle);
#if UNITY_EDITOR || DEVELOPER_BUILD
        GUILayout.Label($"▸ 总分配: {totalAllocatedMB} MB | 预留: {reservedTotalMB} MB", labelStyle);
#endif
        
        // 状态指示
        string status = currentMemoryMB < warningThresholdMB ? "✅ 正常" : 
                       (currentMemoryMB < dangerThresholdMB ? "⚠️ 偏高" : "🔴 危险");
        GUILayout.Label($"▸ 状态: {status}", labelStyle);

        GUILayout.Space(8);
        
        // ========== 场景切换泄漏检测 ==========
        GUI.color = new Color(1f, 0.5f, 0.9f); // 粉色标题
        GUILayout.Label("【场景泄漏检测】", smallLabelStyle);
        
        // 泄漏状态显示
        GUI.color = leakStatusColor;
        GUILayout.Label($"状态: {leakStatus}", labelStyle);
        
        // 快照信息
        GUI.color = new Color(0.85f, 0.85f, 0.85f); // 灰色
        if (snapshotBefore != null)
        {
            GUILayout.Label($"快照A: {snapshotBefore.name}", smallLabelStyle);
            GUILayout.Label($"  堆:{snapshotBefore.monoHeapMB}MB | 已用:{snapshotBefore.monoUsedMB}MB | 总分配:{snapshotBefore.totalAllocatedMB}MB", smallLabelStyle);
        }
        else
        {
            GUILayout.Label("快照A: [未记录] - 点击'记录进场景'", smallLabelStyle);
        }
        
        if (snapshotAfter != null)
        {
            GUILayout.Label($"快照B: {snapshotAfter.name}", smallLabelStyle);
            GUILayout.Label($"  堆:{snapshotAfter.monoHeapMB}MB | 已用:{snapshotAfter.monoUsedMB}MB | 总分配:{snapshotAfter.totalAllocatedMB}MB", smallLabelStyle);
            
            // 显示差值
            if (snapshotBefore != null && snapshotAfter != null)
            {
                long heapDiff = snapshotAfter.monoHeapMB - snapshotBefore.monoHeapMB;
                long usedDiff = snapshotAfter.monoUsedMB - snapshotBefore.monoUsedMB;
                long allocDiff = snapshotAfter.totalAllocatedMB - snapshotBefore.totalAllocatedMB;
                
                string diffColor = (usedDiff > leakWarningMB) ? "<color=red>" : 
                                  ((usedDiff > 0) ? "<color=yellow>" : "<color=green>");
                GUILayout.Label($"差值: 堆{heapDiff:+#;-#;0}MB | 已用{diffColor}{usedDiff:+#;-#;0}MB</color> | 分配{allocDiff:+#;-#;0}MB", smallLabelStyle);
            }
        }
        else
        {
            GUILayout.Label("快照B: [未记录] - 点击'记录出场景'", smallLabelStyle);
        }
        
        // 操作按钮
        GUILayout.Space(3);
        GUI.color = new Color(0.5f, 0.8f, 1f);
        if (GUILayout.Button("📸 记录进场景前 (快照A)", GUILayout.Height(28)))
        {
            SnapshotBeforeEnterScene();
        }
        GUI.color = new Color(1f, 0.8f, 0.5f);
        if (GUILayout.Button("📸 记录出场景后 (快照B)", GUILayout.Height(28)))
        {
            SnapshotAfterLeaveScene();
        }
        GUI.color = new Color(0.8f, 0.8f, 0.8f);
        if (GUILayout.Button("🔄 重置快照", GUILayout.Height(25)))
        {
            ClearSnapshots();
        }
        
        GUILayout.Space(8);

        // ========== 系统物理内存 ==========
        GUI.color = new Color(1f, 0.8f, 0.3f); // 金色标题
        GUILayout.Label("【系统物理内存】", smallLabelStyle);
        
        // 系统使用率根据百分比变色
        if (systemUsagePercent > 90f)
        {
            GUI.color = Color.red;
        }
        else if (systemUsagePercent > 75f)
        {
            GUI.color = new Color(1f, 0.6f, 0f); // 橙色
        }
        else
        {
            GUI.color = Color.green;
        }
        
        GUILayout.Label($"▸ 使用率: {systemUsagePercent:F1}%", labelStyle);
        
        //GUI.color = Color.white;
        GUILayout.Label($"▸ 已用: {systemUsedMemoryMB}MB | 可用: {systemFreeMemoryMB}MB", labelStyle);
        GUILayout.Label($"▸ 总计: {systemTotalMemoryMB} MB ({systemTotalMemoryMB / 1024f:F1} GB)", labelStyle);
        
        // 内存条可视化
        DrawMemoryBar(width - 30f, 18f);
        
        GUILayout.Space(23);
        
        // 阈值信息
        GUI.color = new Color(0.7f, 1.0f, 0.0f); // 灰色
        GUILayout.Label($"警告:{warningThresholdMB}MB 危险:{dangerThresholdMB}MB 泄漏阈值:{leakWarningMB}MB", smallLabelStyle);

        GUILayout.EndArea();
    }

    /// <summary>
    /// 绘制系统内存使用进度条
    /// </summary>
    void DrawMemoryBar(float barWidth, float barHeight)
    {
        float x = 15f;
        float y = GUILayoutUtility.GetLastRect().yMax + 5f;
        Rect barRect = new Rect(x, y, barWidth, barHeight);
        
        // 背景（深色）
        GUI.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);
        GUI.DrawTexture(barRect, Texture2D.whiteTexture);
        
        // 已使用部分
        float usedWidth = barWidth * (systemUsagePercent / 100f);
        Rect usedRect = new Rect(x, y, usedWidth, barHeight);
        
        if (systemUsagePercent > 90f)
        {
            GUI.color = new Color(0.95f, 0.3f, 0.3f); // 红
        }
        else if (systemUsagePercent > 75f)
        {
            GUI.color = new Color(0.95f, 0.65f, 0.15f); // 橙
        }
        else if (systemUsagePercent > 50f)
        {
            GUI.color = new Color(0.85f, 0.85f, 0.2f); // 黄
        }
        else
        {
            GUI.color = new Color(0.3f, 0.85f, 0.35f); // 绿
        }
        GUI.DrawTexture(usedRect, Texture2D.whiteTexture);
        
        // 文字显示百分比
        GUI.color = Color.white;
        if (percentStyle == null) InitStyles();
        GUI.Label(barRect, $"{systemUsagePercent:F0}%", percentStyle);
        
        GUI.color = Color.white;
    }

    /// <summary>
    /// 绘制内存历史曲线图
    /// </summary>
    void DrawMemoryGraph(float graphWidth, float graphHeight)
    {
        if (memoryHistory.Count < 2) return;

        Rect graphRect = new Rect(10, 140, graphWidth, graphHeight);
        
        // 背景
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(graphRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        // 警告线
        float warningY = graphRect.yMax - (float)dangerThresholdMB / (float)(dangerThresholdMB * 1.2f) * graphHeight;
        GUI.color = new Color(1f, 0f, 0f, 0.5f);
        Drawing.DrawLine(graphRect.x, warningY, graphRect.xMax, warningY, Color.red, 1f);
        
        // 绘制曲线
        if (memoryHistory.Count >= 2)
        {
            float maxVal = (float)(dangerThresholdMB * 1.2f); // Y轴最大值
            
            for (int i = 1; i < memoryHistory.Count; i++)
            {
                float x1 = graphRect.x + ((float)(i - 1) / (maxHistoryCount - 1)) * graphWidth;
                float y1 = graphRect.yMax - ((float)memoryHistory[i - 1] / maxVal) * graphHeight;
                float x2 = graphRect.x + ((float)i / (maxHistoryCount - 1)) * graphWidth;
                float y2 = graphRect.yMax - ((float)memoryHistory[i] / maxVal) * graphHeight;

                // 根据值选择颜色
                Color lineColor = memoryHistory[i] > dangerThresholdMB ? Color.red :
                                 (memoryHistory[i] > warningThresholdMB ? Color.yellow : Color.green);
                
                Drawing.DrawLine(x1, y1, x2, y2, lineColor, 2f);
            }
        }

        // 显示数值范围
        GUI.color = Color.white;
        GUI.Label(new Rect(graphRect.xMax - 50, graphRect.y - 15, 50, 20), $"{(int)(dangerThresholdMB * 1.2)}MB");
        GUI.Label(new Rect(graphRect.xMax - 40, graphRect.yMax - 15, 40, 20), "0MB");
    }

    void InitStyles()
    {
        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        smallLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter,
            normal = {
                textColor = Color.white,
                background = CreateGradientTexture(new Color(0.1f, 0.1f, 0.2f, 0.85f))
            }
        };

        percentStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        };
    }

    /// <summary>
    /// 创建渐变纹理用于背景
    /// </summary>
    static Texture2D CreateGradientTexture(Color color)
    {
        Texture2D tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    // ========== 公开方法 ==========

    /// <summary>
    /// 重置峰值内存记录
    /// </summary>
    public void ResetPeakMemory()
    {
        peakMemoryMB = currentMemoryMB;
    }

    /// <summary>
    /// 手动触发内存清理
    /// </summary>
    public void ForceCleanup()
    {
        Resources.UnloadUnusedAssets();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        
        // 等待一帧后重新检测
        StartCoroutine(RefreshAfterCleanup());
        Debug.Log("✅ 已手动触发内存清理");
    }

    System.Collections.IEnumerator RefreshAfterCleanup()
    {
        yield return null;
        CheckMemory(); // 重新获取最新数据
    }

    /// <summary>
    /// 切换显示/隐藏
    /// </summary>
    public void ToggleVisibility()
    {
        showGUI = !showGUI;
    }
    
    // ========== 场景泄漏检测方法 ==========
    
    /// <summary>
    /// 记录进场景前的内存快照（在加载新场景前调用）
    /// </summary>
    /// <param name="sceneName">场景名称，用于标识</param>
    public void SnapshotBeforeEnterScene(string sceneName = "进场景前")
    {
        // 先执行一次清理，确保基准准确
        Resources.UnloadUnusedAssets();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        
        // 等待一帧再采集数据
        StartCoroutine(TakeSnapshotDelayed(true, sceneName));
    }
    
    /// <summary>
    /// 记录出场景后的内存快照（在离开场景后调用）
    /// </summary>
    /// <param name="sceneName">场景名称，用于标识</param>
    public void SnapshotAfterLeaveScene(string sceneName = "出场景后")
    {
        // 先执行清理
        Resources.UnloadUnusedAssets();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        
        // 等待一帧再采集数据
        StartCoroutine(TakeSnapshotDelayed(false, sceneName));
    }
    
    private System.Collections.IEnumerator TakeSnapshotDelayed(bool isBefore, string sceneName)
    {
        yield return null; // 等待一帧
        
        // 重新采集最新数据
        CheckMemory();
        
        var snapshot = new MemorySnapshot
        {
            name = sceneName,
            timestamp = DateTime.Now,
            monoHeapMB = monoHeapSizeMB,
            monoUsedMB = monoUsedSizeMB,
            managedHeapMB = managedHeapMB,
            totalAllocatedMB = totalAllocatedMB,
            processMemoryMB = processPrivateMemoryMB
        };
        
        if (isBefore)
        {
            snapshotBefore = snapshot;
            snapshotAfter = null; // 清除旧的结果
            leakStatus = "已记录进场景前，请切换场景后再点击'记录出场景后'";
            leakStatusColor = Color.yellow;
            
            Debug.Log($"📸 [MemoryMonitor] 已记录进场景前快照:\n{snapshot}");
        }
        else
        {
            snapshotAfter = snapshot;
            
            // 对比分析
            AnalyzeLeakage();
        }
    }
    
    /// <summary>
    /// 分析两次快照之间的内存差异，判断是否有泄漏
    /// </summary>
    private void AnalyzeLeakage()
    {
        if (snapshotBefore == null || snapshotAfter == null) return;
        
        long heapDiff = snapshotAfter.monoHeapMB - snapshotBefore.monoHeapMB;
        long usedDiff = snapshotAfter.monoUsedMB - snapshotBefore.monoUsedMB;
        long allocDiff = snapshotAfter.totalAllocatedMB - snapshotBefore.totalAllocatedMB;
        long processDiff = snapshotAfter.processMemoryMB - snapshotBefore.processMemoryMB;
        
        // 输出详细日志到Console
        Debug.Log("========== 内存泄漏检测结果 ==========");
        Debug.Log($"进场景: {snapshotBefore.ToString()}");
        Debug.Log($"出场景: {snapshotAfter.ToString()}");
        Debug.Log($"差值: Mono堆{heapDiff:+#;-#;0}MB | Mono已用{usedDiff:+#;-#;0}MB | 总分配{allocDiff:+#;-#;0}MB | 进程{processDiff:+#;-#;0}MB");
        Debug.Log($"======================================");
        
        // 判断是否有泄漏（主要看Mono已用大小和总分配）
        if (usedDiff > leakWarningMB || allocDiff > leakWarningMB * 2)
        {
            // 可能存在泄漏
            if (usedDiff > leakWarningMB * 3)
            {
                leakStatus = $"🔴 严重泄漏! +{usedDiff}MB (阈值:{leakWarningMB}MB)";
                leakStatusColor = Color.red;
                Debug.LogError($"🔴 [MemoryMonitor] 检测到严重内存泄漏! Mono已用增加 {usedDiff} MB");
            }
            else
            {
                leakStatus = $"⚠️ 可能有泄漏 +{usedDiff}MB (阈值:{leakWarningMB}MB)";
                leakStatusColor = new Color(1f, 0.6f, 0f); // 橙色
                Debug.LogWarning($"⚠️ [MemoryMonitor] 可能存在内存泄漏, Mono已用增加 {usedDiff} MB");
            }
        }
        else if (usedDiff > 10)
        {
            // 轻微增长（可能是正常的缓存或碎片）
            leakStatus = $"✅ 正常 (+{usedDiff}MB 轻微增长，可接受)";
            leakStatusColor = new Color(1f, 1f, 0.3f); // 黄绿色
            Debug.Log($"✅ [MemoryMonitor] 内存变化正常, 增长 {usedDiff} MB (可接受范围)");
        }
        else if (usedDiff <= 0)
        {
            // 内存下降或持平
            leakStatus = $"✅ 完美 ({usedDiff}MB 无泄漏)";
            leakStatusColor = Color.green;
            Debug.Log($"✅ [MemoryMonitor] 无内存泄漏, 甚至释放了 {Math.Abs(usedDiff)} MB");
        }
    }
    
    /// <summary>
    /// 清除所有快照
    /// </summary>
    public void ClearSnapshots()
    {
        snapshotBefore = null;
        snapshotAfter = null;
        leakStatus = "等待测试...";
        leakStatusColor = Color.gray;
        Debug.Log("[MemoryMonitor] 快照已清除");
    }
    
    /// <summary>
    /// 在代码中快速测试场景切换的便捷方法
    /// 用法：
    ///   monitor.TestSceneTransition("主城", () => { 
    ///       // 加载新场景的代码
    ///       SceneManager.LoadScene("Battle"); 
    ///   });
    /// </summary>
    public System.Collections.IEnumerator TestSceneTransition(string fromScene, System.Action loadAction)
    {
        Debug.Log($"=== 开始场景泄漏测试: {fromScene} → 新场景 ===");
        
        // 记录进场景前
        SnapshotBeforeEnterScene(fromScene);
        yield return new WaitForSeconds(0.5f); // 等待快照完成
        
        // 执行场景切换
        loadAction?.Invoke();
        
        // 等待场景加载完成
        yield return new WaitForSeconds(2f);
        
        // 记录出场景后
        SnapshotAfterLeaveScene("新场景");
        yield return new WaitForSeconds(0.5f);
        
        Debug.Log("=== 场景泄漏测试完成 ===");
    }
}

/// <summary>
/// 简单的绘制工具类
/// </summary>
public static class Drawing
{
    private static Texture2D lineTex = null;
    private static Material lineMat = null;

    static Drawing()
    {
        if (lineMat == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMat = new Material(shader);
            lineMat.hideFlags = HideFlags.HideAndDontSave;
        }
    }

    public static void DrawLine(Vector2 p1, Vector2 p2, Color color, float width)
    {
        DrawLine(p1.x, p1.y, p2.x, p2.y, color, width);
    }

    public static void DrawLine(float x1, float y1, float x2, float y2, Color color, float width)
    {
        if (lineTex == null)
        {
            lineTex = new Texture2D(1, 1);
            lineTex.SetPixel(0, 0, Color.white);
            lineTex.Apply();
        }

        lineMat.SetPass(0);
        GL.PushMatrix();
        GL.LoadPixelMatrix();

        color.a = 1f;
        lineMat.SetColor("_Color", color);
        GL.Begin(GL.LINES);
        GL.Vertex3(x1, y1, 0);
        GL.Vertex3(x2, y2, 0);
        GL.End();
        GL.PopMatrix();
    }
}
