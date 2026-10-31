package main

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"time"
)

// Offline random-header throughput measurement: no node calls or submissions.
func runOfflineBenchmark(w *gpuWorker, duration time.Duration, duty float64) error {
	if duration <= 0 || duration > 30*time.Second {
		return fmt.Errorf("offline benchmark must be bounded to 1ns..30s")
	}
	header := make([]byte, 120)
	if _, err := rand.Read(header); err != nil {
		return err
	}
	nonce := binary.LittleEndian.Uint64(header[112:])
	started := time.Now()
	var hashes, batches uint64
	var initMS, computeMS, finalMS, workerMS float64
	for time.Since(started) < duration {
		active := time.Now()
		reply, err := w.run(header, nonce)
		if err != nil {
			return err
		}
		if batches%32 == 0 {
			i := int(batches % uint64(w.batch))
			if err := checkDigest(header, nonce+uint64(i), reply.Hashes[i]); err != nil {
				return err
			}
		}
		hashes += uint64(w.batch)
		batches++
		nonce += uint64(w.batch)
		initMS += reply.InitMS
		computeMS += reply.ComputeMS
		finalMS += reply.FinalMS
		workerMS += reply.MS
		pause := dutyPause(time.Since(active), duty, 0)
		if pause > 0 {
			time.Sleep(pause)
		}
	}
	elapsed := time.Since(started).Seconds()
	report := map[string]any{"checkedUTC": time.Now().UTC(), "offline": true, "submittedBlocks": 0, "hashes": hashes, "batch": w.batch, "targetDutyPercent": duty, "seconds": elapsed, "endToEndHps": float64(hashes) / elapsed, "workerHps": float64(hashes) / (workerMS / 1000), "workerMS": workerMS, "initMS": initMS, "computeMS": computeMS, "finalMS": finalMS, "batches": batches, "gpuScratchMiB": w.batch * 8}
	b, _ := json.MarshalIndent(report, "", "  ")
	filename := fmt.Sprintf("benchmark-batch%d-duty%.0f.json", w.batch, duty)
	if err := os.WriteFile(filename, b, 0600); err != nil {
		return err
	}
	log.Printf("OFFLINE BENCHMARK %.1f H/s hashes=%d seconds=%.2f batch=%d duty=%.0f%%", float64(hashes)/elapsed, hashes, elapsed, w.batch, duty)
	return nil
}
