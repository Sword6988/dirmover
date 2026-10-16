"""列宽自适应算法的纯函数测试（``docmover.ui.columns``）。

不依赖 Tk：直接喂「内容需要宽度 + 视口宽度」，检查分配规则本身。
界面级的列宽契约在 ``tests/test_ui_smoke.py``（需要真实窗口才能量文字）。
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from docmover.ui import columns  # noqa: E402


class TestLargestRemainder(unittest.TestCase):

    def test_sums_exactly(self):
        self.assertEqual(sum(columns._largest_remainder(100, [1, 1, 1])), 100)
        self.assertEqual(sum(columns._largest_remainder(37, [5, 3, 2])), 37)
        self.assertEqual(columns._largest_remainder(0, [1, 1]), [0, 0])

    def test_remainder_goes_to_largest_fraction(self):
        # 100/3 = 33.33：余数最大者先拿，多出的 1px 给第一列
        self.assertEqual(columns._largest_remainder(100, [1, 1, 1]), [34, 33, 33])

    def test_no_weights_falls_back_to_even_split(self):
        self.assertEqual(sum(columns._largest_remainder(10, [0, 0, 0])), 10)


class TestFitColumns(unittest.TestCase):
    """用真实的实测数据当夹具（975×800 物理像素窗口，DPI 缩放 1.25）。"""

    NEEDS = [105, 288, 317, 209]        # 名称（表头决定）/ 当前 / 目标 / 状态
    MINIMUMS = [105, 90, 90, 209]       # 名称与状态为保护列：下限 = 内容需要
    QUOTAS = [30, 175, 175, 90]
    KEYS = columns.COLUMN_KEYS

    def fit(self, available: int):
        widths = columns.fit_columns(self.NEEDS, self.MINIMUMS, self.QUOTAS,
                                     available)
        return dict(zip(self.KEYS, widths))

    # ---------------------------------------------------------- 总和
    def test_sum_equals_available_across_viewports(self):
        """任意视口宽度下，各列之和 == 可用宽（不裁也不留空）。"""
        for available in range(500, 4000, 37):
            widths = self.fit(available)
            self.assertEqual(sum(widths.values()), available,
                             msg=f"available={available} → {widths}")

    def test_equal_case_returns_content_widths(self):
        widths = self.fit(sum(self.NEEDS))
        self.assertEqual([widths[k] for k in self.KEYS], self.NEEDS)

    def test_tiny_viewport_is_total_and_non_negative(self):
        """极端窄窗（被 minsize 挡住的病态输入）也必须返回可用的宽度。

        注意：可用宽不足「列数」像素时，每列 1px 已是极限（不可能再有更窄的列），
        此时总和会略大于可用宽——这是本函数唯一不满足「总和 == 可用宽」的输入域。
        """
        n = len(self.KEYS)
        for available in (0, 1, 50, 200, 400):
            widths = self.fit(available)
            self.assertTrue(all(w >= 1 for w in widths.values()), msg=str(widths))
            if available >= n:
                self.assertTrue(all(w <= available for w in widths.values()),
                                msg=str(widths))
                self.assertEqual(sum(widths.values()), available, msg=str(widths))

    def test_single_column_gets_everything(self):
        self.assertEqual(columns.fit_columns([100], [50], [10], 400), [400])
        self.assertEqual(columns.fit_columns([], [], [], 400), [])

    # ------------------------------------------------------ 余量分配
    def test_surplus_goes_to_path_columns_by_quota(self):
        """有多余空间时，路径列按配额拿大头，名称列只拿极少的配额。"""
        available = sum(self.NEEDS) + sum(self.QUOTAS)
        widths = self.fit(available)
        gains = {k: widths[k] - need for k, need in zip(self.KEYS, self.NEEDS)}
        for key, quota in zip(self.KEYS, self.QUOTAS):
            self.assertEqual(gains[key], quota, msg=str(gains))
        # 名称列的余量必须显著小于路径列（历史缺陷：它和白占大块空白等价）
        self.assertLess(gains["name"], gains["current"] / 3)
        self.assertLess(gains["name"], gains["target"] / 3)

    def test_huge_viewport_still_fills_width(self):
        widths = self.fit(6000)
        self.assertEqual(sum(widths.values()), 6000)
        self.assertTrue(all(w > 0 for w in widths.values()))

    # -------------------------------------------------------- 压缩
    def test_deficit_comes_out_of_path_columns_only(self):
        """视口不足时路径列让步；名称与状态（保护列）一个字都不裁。"""
        available = sum(self.NEEDS) - 60
        widths = self.fit(available)
        self.assertEqual(widths["name"], self.NEEDS[0])
        self.assertEqual(widths["status"], self.NEEDS[3])
        self.assertLess(widths["current"], self.NEEDS[1])
        self.assertLess(widths["target"], self.NEEDS[2])
        self.assertEqual(sum(widths.values()), available)

    def test_deficit_prefers_columns_with_more_slack(self):
        """让步量与「可让余量」成正比：余量大的列先让，余量小的列少让。"""
        available = sum(self.NEEDS) - 40
        widths = self.fit(available)
        current_cut = self.NEEDS[1] - widths["current"]
        target_cut = self.NEEDS[2] - widths["target"]
        slack_current = self.NEEDS[1] - self.MINIMUMS[1]
        slack_target = self.NEEDS[2] - self.MINIMUMS[2]
        self.assertGreater(target_cut, current_cut)      # 目标列余量更大 → 让更多
        self.assertAlmostEqual(current_cut / slack_current,
                               target_cut / slack_target, delta=0.02)

    def test_flexible_column_stops_at_minimum(self):
        """非保护列最多让到硬下限，再不够才轮到整体等比压缩。"""
        widths = self.fit(sum(self.MINIMUMS) + 1)
        self.assertGreaterEqual(widths["current"], self.MINIMUMS[1])
        self.assertGreaterEqual(widths["target"], self.MINIMUMS[2])

    # -------------------------------------------------------- 确定性
    def test_deterministic(self):
        for available in (865, 1169, 1750, 2400):
            self.assertEqual(self.fit(available), self.fit(available))

    def test_column_table_is_consistent_with_helpers(self):
        """列定义与派生常量必须同步（表头映射 / key 顺序）。"""
        self.assertEqual(tuple(c[0] for c in columns.COLUMNS), columns.COLUMN_KEYS)
        self.assertEqual(set(columns.HEADERS), set(columns.COLUMN_KEYS))
        self.assertEqual(columns.HEADERS["status"], "状态")


if __name__ == "__main__":
    unittest.main()
