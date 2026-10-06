import unittest

from simulate_fanout import Target, simulate


class FanoutModelTests(unittest.TestCase):
    def test_slow_branch_holds_shared_blocks_and_throttles_fast_one(self):
        solo = simulate(512, 1600, [Target("fast", 1000)])
        mixed = simulate(512, 1600, [Target("fast", 1000), Target("slow", 40)])
        self.assertLess(solo["destinations"]["fast"]["finished_s"], 1)
        self.assertGreater(mixed["destinations"]["fast"]["finished_s"], 5)
        self.assertEqual(mixed["peak_pool_mib"], 256)
        self.assertLess(mixed["destinations"]["fast"]["finished_s"], mixed["job_finished_s"])

    def test_stalled_usb_stops_new_reads_after_pool_fills(self):
        run = simulate(512, 1600, [Target("fast", 1000), Target("slow", 40, 2, 7)])
        at_three = next(s for s in run["samples"] if s["second"] == 3)
        at_six = next(s for s in run["samples"] if s["second"] == 6)
        self.assertEqual(at_three["source_mib"], at_six["source_mib"])
        self.assertEqual(at_three["pool_mib"], 256)

    def test_spool_capacity_changes_fast_completion_but_not_slow_job_completion(self):
        small = simulate(1024, 1600, [Target("fast", 1000), Target("slow", 40)],
                         spool_target="slow", spool_capacity=256)
        large = simulate(1024, 1600, [Target("fast", 1000), Target("slow", 40)],
                         spool_target="slow", spool_capacity=1024)
        self.assertLess(large["destinations"]["fast"]["finished_s"], 2)
        self.assertGreater(small["destinations"]["fast"]["finished_s"], 10)
        self.assertAlmostEqual(small["job_finished_s"], large["job_finished_s"], delta=.1)
        self.assertLessEqual(large["peak_spool_mib"], 1024)

    def test_source_rate_still_limits_a_fast_destination(self):
        run = simulate(1024, 400, [Target("fast", 1000)])
        self.assertGreaterEqual(run["destinations"]["fast"]["finished_s"], 1024 / 400)

    def test_single_outstanding_io_exposes_per_block_latency_cost(self):
        ideal = simulate(1024, 2000, [Target("fast", 1500)])
        delayed = simulate(1024, 2000, [Target("fast", 1500, latency_ms=10)])
        self.assertGreater(delayed["destinations"]["fast"]["finished_s"],
                           ideal["destinations"]["fast"]["finished_s"] * 2)

    def test_same_targets_can_be_reused_for_an_ab_comparison(self):
        targets = [Target("fast", 1000), Target("slow", 40)]
        first = simulate(512, 1600, targets)
        second = simulate(512, 1600, targets)
        self.assertEqual(first["destinations"], second["destinations"])


if __name__ == "__main__":
    unittest.main()
