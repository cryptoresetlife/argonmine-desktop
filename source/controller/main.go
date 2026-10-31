package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"math/big"
	"net/http"
 "net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"argonmine/consensus"
	"argonmine/miner"
	"argonmine/wallet"
)

const payoutDefault = ""

// Deployment-specific physical identity, set at link time for remote builds.
// The local build keeps its original fixed RTX5090 identity.
var expectedGPUUUID = ""

type workerReply struct {
	Ready     bool     `json:"ready"`
	UUID      string   `json:"uuid"`
	Batch     int      `json:"batch"`
	Hashes    []string `json:"hashes"`
	MS        float64  `json:"ms"`
	InitMS    float64  `json:"initMS"`
	ComputeMS float64  `json:"computeMS"`
	FinalMS   float64  `json:"finalMS"`
}
type gpuWorker struct {
	cmd   *exec.Cmd
	in    io.WriteCloser
	out   *bufio.Scanner
	batch int
}

func startWorker(exe string, batch int, mode ...string) (*gpuWorker, error) {
	args := append([]string{strconv.Itoa(batch)}, mode...)
	cmd := exec.Command(exe, args...)
	cmd.Stderr = os.Stderr
	in, err := cmd.StdinPipe()
	if err != nil {
		return nil, err
	}
	out, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	if err = cmd.Start(); err != nil {
		return nil, err
	}
	w := &gpuWorker{cmd: cmd, in: in, out: bufio.NewScanner(out), batch: batch}
	w.out.Buffer(make([]byte, 4096), 2<<20)
	var r workerReply
	if err = w.read(&r); err != nil {
		w.close()
		return nil, err
	}
	if !r.Ready || len(r.UUID) != 32 || (expectedGPUUUID != "" && r.UUID != expectedGPUUUID) || r.Batch < 64 || r.Batch > batch || r.Batch & (r.Batch-1) != 0 {
		w.close()
		return nil, fmt.Errorf("unexpected GPU startup identity")
	}
	w.batch = r.Batch
	expectedGPUUUID = r.UUID
	return w, nil
}
func (w *gpuWorker) read(r *workerReply) error {
	if !w.out.Scan() {
		return fmt.Errorf("GPU worker ended: %v", w.out.Err())
	}
	return json.Unmarshal(w.out.Bytes(), r)
}
func (w *gpuWorker) run(header []byte, nonce uint64) (*workerReply, error) {
	if len(header) != 120 {
		return nil, fmt.Errorf("unexpected header size")
	}
	if _, err := fmt.Fprintf(w.in, "%s %d\n", hex.EncodeToString(header), nonce); err != nil {
		return nil, err
	}
	var r workerReply
	done := make(chan error, 1)
	go func() { done <- w.read(&r) }()
	select {
	case err := <-done:
		if err != nil {
			return nil, err
		}
	case <-time.After(5 * time.Second):
		w.cmd.Process.Kill()
		return nil, fmt.Errorf("GPU batch exceeded 5 second deadline; stopped for investigation")
	}
	if len(r.Hashes) != w.batch {
		return nil, fmt.Errorf("wrong result count")
	}
	return &r, nil
}
func (w *gpuWorker) close() {
	fmt.Fprintln(w.in, "quit")
	w.in.Close()
	done := make(chan error, 1)
	go func() { done <- w.cmd.Wait() }()
	select {
	case <-done:
		return
	case <-time.After(5 * time.Second):
		if w.cmd.Process != nil {
			w.cmd.Process.Kill()
		}
		select {
		case <-done:
		case <-time.After(2 * time.Second):
		}
	}
}
func checkDigest(header []byte, nonce uint64, s string) error {
	if len(s) != 64 {
		return fmt.Errorf("invalid GPU digest size")
	}
	h := append([]byte{}, header...)
	binary.LittleEndian.PutUint64(h[112:], nonce)
	cpu := consensus.PowHash(h)
	if hex.EncodeToString(cpu[:]) != s {
		return fmt.Errorf("GPU/CPU mismatch for nonce %d: gpu=%s cpu=%x", nonce, s, cpu)
	}
	return nil
}
func selfTest(w *gpuWorker, rounds int) error {
	nonces := []uint64{0, 0x8000000000000000, ^uint64(0) - uint64(w.batch/2), 0x0123456789abcdef}
	start := time.Now()
	var gpuMS float64
	for r := 0; r < rounds; r++ {
		header := make([]byte, 120)
		if _, err := rand.Read(header); err != nil {
			return err
		}
		if r == 0 {
			clear(header)
		}
		reply, err := w.run(header, nonces[r%len(nonces)])
		if err != nil {
			return err
		}
		gpuMS += reply.MS
		for i, s := range reply.Hashes {
			if err := checkDigest(header, nonces[r%len(nonces)]+uint64(i), s); err != nil {
				return err
			}
		}
	}
	var h consensus.Hash
	for i := range h {
		h[i] = byte(i)
	}
	n := consensus.HashToBig(h)
	want := new(big.Int)
	for i := 31; i >= 0; i-- {
		want.Lsh(want, 8)
		want.Or(want, big.NewInt(int64(h[i])))
	}
	if n.Cmp(want) != 0 {
		return fmt.Errorf("little-endian target check failed")
	}
	report := map[string]any{"checkedUTC": time.Now().UTC(), "passed": true, "vectors": rounds * w.batch, "batch": w.batch, "gpuBatchTotalMS": gpuMS, "gpuBackendHps": float64(rounds*w.batch) / (gpuMS / 1000), "totalSeconds": time.Since(start).Seconds(), "algorithm": "Argon2id v19 t=1 m=8192KiB p=1 out=32 salt=Argonmine/pow/v1", "gpuUUIDHex": expectedGPUUUID}
	b, _ := json.MarshalIndent(report, "", "  ")
	if err := os.WriteFile("selftest.json", b, 0600); err != nil {
		return err
	}
	log.Printf("SELFTEST PASS: %d GPU hashes match official CPU reference, %.1f backend H/s", rounds*w.batch, float64(rounds*w.batch)/(gpuMS/1000))
	return nil
}

type rpcClient struct {
	url, token string
	hc         *http.Client
}

func (c *rpcClient) call(method string, params ...any) (json.RawMessage, error) {
	if params == nil {
		params = []any{}
	}
	b, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "id": 1, "method": method, "params": params})
	req, err := http.NewRequest("POST", c.url, bytes.NewReader(b))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+c.token)
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("node HTTP %d", resp.StatusCode)
	}
	var answer struct {
		Result json.RawMessage `json:"result"`
		Error  any             `json:"error"`
	}
	if err = json.NewDecoder(io.LimitReader(resp.Body, 16<<20)).Decode(&answer); err != nil {
		return nil, err
	}
	if answer.Error != nil {
		return nil, fmt.Errorf("RPC %s: %v", method, answer.Error)
	}
	return answer.Result, nil
}
func makeBlock(c *rpcClient, payout consensus.TxOut) (*consensus.Block, uint32, error) {
	raw, err := c.call("getblocktemplate")
	if err != nil {
		return nil, 0, err
	}
	var t miner.TemplateJSON
	if err = json.Unmarshal(raw, &t); err != nil {
		return nil, 0, err
	}
	outputs := []consensus.TxOut{{Amount: t.CoinbaseValue, Type: payout.Type, Payload: payout.Payload}}
	if treasury, ok := consensus.TreasuryOutput(consensus.MainNet, t.Height); ok {
		outputs = append(outputs, treasury)
		t.CoinbaseValue += treasury.Amount
	}
	block, err := miner.AssembleFromTemplateOutputs(&t, outputs, []byte("local-rtx5090-cuda"), time.Now().Unix())
	if err != nil {
		return nil, 0, err
	}
	if err = consensus.CheckTreasury(consensus.MainNet, block.Txs[0], t.Height); err != nil {
		return nil, 0, err
	}
	return block, t.Height, nil
}
func dutyPause(active time.Duration, duty float64, floor time.Duration) time.Duration {
	if duty <= 0 || duty >= 100 {
		return floor
	}
	calculated := time.Duration(float64(active) * (100 - duty) / duty)
	if calculated < floor {
		return floor
	}
	return calculated
}
func mine(w *gpuWorker, c *rpcClient, payout consensus.TxOut, address string, duration time.Duration, pause time.Duration, duty float64) error {
	raw, err := c.call("getblockchaininfo")
	if err != nil {
		return err
	}
	var info struct {
		Genesis string `json:"genesis"`
		Syncing bool   `json:"initialblockdownload"`
	}
	if err = json.Unmarshal(raw, &info); err != nil {
		return err
	}
	if info.Genesis != consensus.MainNet.GenesisHash.String() || info.Syncing {
		return fmt.Errorf("node is not synced on expected mainnet")
	}
	var hashes, accepted, batches uint64
	var activeTime, sleepTime time.Duration
	start := time.Now()
	lastLog := start
	var lastHeight uint32
	for duration <= 0 || time.Since(start) < duration {
		if _, err = os.Stat("STOPPED"); err == nil {
			return nil
		}
		block, height, err := makeBlock(c, payout)
		if err != nil {
			return err
		}
		lastHeight = height
		target, err := consensus.CompactToTarget(block.Header.Bits)
		if err != nil {
			return err
		}
		if target.Cmp(consensus.MainNet.PowLimit) > 0 {
			return fmt.Errorf("target exceeds mainnet limit")
		}
		var seed [8]byte
		if _, err = rand.Read(seed[:]); err != nil {
			return err
		}
		nonce := binary.LittleEndian.Uint64(seed[:])
		refresh := time.Now()
		for time.Since(refresh) < 2*time.Second && (duration <= 0 || time.Since(start) < duration) {
			if _, err = os.Stat("STOPPED"); err == nil {
				return nil
			}
			hdr := block.Header.Serialize()
			activeStart := time.Now()
			reply, err := w.run(hdr, nonce)
			if err != nil {
				return err
			}
			activeElapsed := time.Since(activeStart)
			activeTime += activeElapsed
			hashes += uint64(w.batch)
			batches++
			if batches == 1 || batches%64 == 0 {
				if err = checkDigest(hdr, nonce, reply.Hashes[0]); err != nil {
					return err
				}
			}
			solved := false
			for i, s := range reply.Hashes {
				b, err := hex.DecodeString(s)
				if err != nil || len(b) != 32 {
					return fmt.Errorf("invalid GPU hash")
				}
				var h consensus.Hash
				copy(h[:], b)
				if consensus.HashToBig(h).Cmp(target) > 0 {
					continue
				}
				block.Header.Nonce = nonce + uint64(i)
				if err = checkDigest(hdr, block.Header.Nonce, s); err != nil {
					return err
				}
				if err = consensus.CheckProofOfWork(&block.Header, consensus.MainNet.PowLimit); err != nil {
					return err
				}
				hash := block.Header.Hash().String()
				record := map[string]any{"hash": hash, "height": height, "payout": address, "blockHex": hex.EncodeToString(block.Serialize()), "foundUTC": time.Now().UTC()}
				recordBytes, _ := json.Marshal(record)
				if err = os.WriteFile(filepath.Join("candidates", hash+".json"), recordBytes, 0600); err != nil {
					return err
				}
				verdict, err := c.call("submitblock", hex.EncodeToString(block.Serialize()))
				if err != nil {
					return fmt.Errorf("submission outcome uncertain; candidate persisted: %s: %w", hash, err)
				}
				log.Printf("BLOCK SUBMIT hash=%s height=%d result=%s", hash, height, verdict)
				record["verdict"] = string(verdict)
				record["submittedUTC"] = time.Now().UTC()
				if strings.TrimSpace(string(verdict)) == "null" {
					canon, err := c.call("getblockhash", height)
					if err != nil {
						return err
					}
					var canonical string
					if err = json.Unmarshal(canon, &canonical); err != nil {
						return err
					}
					record["canonicalAtSubmission"] = canonical == hash
					if canonical == hash {
						accepted++
						log.Printf("BLOCK ACCEPTED hash=%s height=%d", hash, height)
					}
				}
				recordBytes, _ = json.Marshal(record)
				if err = os.WriteFile(filepath.Join("candidates", hash+".json"), recordBytes, 0600); err != nil {
					return err
				}
				solved = true
				break
			}
			nonce += uint64(w.batch)
			rest := dutyPause(activeElapsed, duty, pause)
			if rest > 0 {
				sleepStart := time.Now()
				time.Sleep(rest)
				sleepTime += time.Since(sleepStart)
			}
			if solved {
				break
			}
			if time.Since(lastLog) >= 10*time.Second {
				measuredDuty := 100 * float64(activeTime) / float64(activeTime+sleepTime)
				status := map[string]any{"checkedUTC": time.Now().UTC(), "hashes": hashes, "averageHps": float64(hashes) / time.Since(start).Seconds(), "acceptedBlocks": accepted, "height": lastHeight, "payout": address, "node": c.url, "batch": w.batch, "targetBatchDutyPercent": duty, "measuredBatchDutyPercent": measuredDuty, "activeBatchMS": float64(activeTime) / float64(time.Millisecond), "yieldMS": float64(sleepTime) / float64(time.Millisecond), "gpuScratchMiB": w.batch * 8}
				b, _ := json.MarshalIndent(status, "", "  ")
				if err = os.WriteFile("mining-status.json", b, 0600); err != nil {
					return err
				}
				log.Printf("MINING %.1f H/s hashes=%d accepted=%d height=%d batch-duty=%.1f%% target=%.1f%%", float64(hashes)/time.Since(start).Seconds(), hashes, accepted, lastHeight, measuredDuty, duty)
				lastLog = time.Now()
			}
		}
	}
	log.Printf("TRIAL COMPLETE %.1f H/s hashes=%d accepted=%d", float64(hashes)/time.Since(start).Seconds(), hashes, accepted)
	return nil
}
func main() {
	log.SetFlags(log.LstdFlags | log.Lmicroseconds)
	workerExe := flag.String("worker", "bin/argon-gpu-worker.exe", "local CUDA worker path")
	flag.StringVar(&expectedGPUUUID, "gpu-uuid", "", "optional expected physical CUDA UUID, 32 lowercase hex characters")
	batch := flag.Int("batch", 256, "GPU batch size")
	testOnly := flag.Bool("selftest", false, "offline CPU/GPU comparison only")
	rounds := flag.Int("rounds", 4, "offline vector batches")
	rpc := flag.String("rpc", "http://127.0.0.1:19342/", "loopback SSH tunnel to own node")
	cookie := flag.String("cookie", "secrets/remote-rpc.cookie", "local restricted RPC authentication file")
	address := flag.String("address", payoutDefault, "public mining payout")
	duration := flag.Duration("duration", 0, "trial duration, 0 continuous until STOPPED")
	pause := flag.Duration("pause", 0, "minimum pause between GPU batches")
	duty := flag.Float64("duty", 80, "target percentage of batch compute time, 100 disables duty throttling")
	benchmark := flag.Duration("benchmark", 0, "bounded offline benchmark duration after self-test")
	workerMode := flag.String("worker-mode", "gpu", "gpu or legacy worker initialization")
	checkAddress := flag.Bool("check-address", false, "validate payout address without starting CUDA or node")
 flag.Parse()
 if *checkAddress { if _, err := wallet.DecodeAddress(consensus.MainNet.HRP, *address); err != nil { log.Fatal("invalid ARG mainnet address") }; fmt.Println("address valid"); return }
	if *duty <= 0 || *duty > 100 || *pause < 0 {
		log.Fatal("invalid duty/pause")
	}
	if !*testOnly {
		if _, err := os.Stat("STOPPED"); err == nil {
			log.Fatal("STOPPED marker exists")
		}
		if *address == "" {
			log.Fatal("supply your own mainnet payout with -address")
		}
		u, parseErr := url.Parse(*rpc)
 if parseErr != nil || u.Scheme != "http" || u.Hostname() != "127.0.0.1" || u.User != nil || u.Port() == "" || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") {
			log.Fatal("node must be own loopback RPC")
		}
	}
	if err := os.MkdirAll("candidates", 0700); err != nil {
		log.Fatal(err)
	}
	w, err := startWorker(*workerExe, *batch, *workerMode)
	if err != nil {
		log.Fatal(err)
	}
	defer w.close()
	if *rounds < 1 || *rounds > 8 {
		log.Fatal("rounds must be 1..8")
	}
	if err = selfTest(w, *rounds); err != nil {
		os.WriteFile("HALTED", []byte(err.Error()), 0600)
		log.Fatal(err)
	}
	if *testOnly {
		if *benchmark > 0 {
			if err := runOfflineBenchmark(w, *benchmark, *duty); err != nil {
				log.Fatal(err)
			}
		}
		return
	}
	if _, err = os.Stat("HALTED"); err == nil {
		log.Fatal("HALTED marker exists; investigate before resuming")
	}
	payout, err := wallet.DecodeAddress(consensus.MainNet.HRP, *address)
	if err != nil {
		log.Fatal(err)
	}
	token, err := os.ReadFile(*cookie)
	if err != nil {
		log.Fatal(err)
	}
	c := &rpcClient{url: *rpc, token: strings.TrimSpace(string(token)), hc: &http.Client{Timeout: 10 * time.Second}}
	if err = mine(w, c, payout, *address, *duration, *pause, *duty); err != nil {
		os.WriteFile("HALTED", []byte(err.Error()), 0600)
		log.Fatal(err)
	}
}


