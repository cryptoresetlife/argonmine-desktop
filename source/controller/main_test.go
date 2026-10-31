package main

import (
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"testing"
	"time"

	"argonmine/chain"
	"argonmine/consensus"
	"argonmine/mempool"
	"argonmine/miner"
	"argonmine/wallet"
)

func TestDuty80LeavesOneQuarterOfComputeTimeForDesktop(t *testing.T) {
	for _, active := range []time.Duration{time.Millisecond, 10 * time.Millisecond, 100 * time.Millisecond, 4 * time.Second} {
		pause := dutyPause(active, 80, 0)
		if pause != active/4 {
			t.Fatalf("80%% duty for %v scheduled %v pause", active, pause)
		}
		ratio := float64(active) / float64(active+pause)
		if ratio < 0.799 || ratio > 0.801 {
			t.Fatalf("wrong scheduled duty: %v", ratio)
		}
	}
	if dutyPause(time.Millisecond, 80, 20*time.Millisecond) != 20*time.Millisecond {
		t.Fatal("explicit longer desktop pause must be retained")
	}
	if dutyPause(time.Millisecond, 100, 0) != 0 {
		t.Fatal("100 percent must preserve unthrottled behavior")
	}
}

func TestMainnetTemplatePaysAuthorizedWalletAndTreasury(t *testing.T) {
	payout, err := wallet.DecodeAddress(consensus.MainNet.HRP, "arg1qt8m4tkg5a2yvxlqumdvvuqza8nekrfzk8dhu6cnh5af9cy55atsq7zefrm")
	if err != nil {
		t.Fatal(err)
	}
	fixture := miner.TemplateJSON{Version: 1, PreviousBlockHash: consensus.MainNet.GenesisHash.String(), Height: 301, Bits: "1e400000", CurTime: uint64(time.Now().Unix()), MinTime: uint64(time.Now().Unix()), CoinbaseValue: 4_950_001_000}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		json.NewEncoder(w).Encode(map[string]any{"jsonrpc": "2.0", "id": 1, "result": fixture})
	}))
	defer server.Close()
	client := &rpcClient{url: server.URL, hc: server.Client()}
	block, height, err := makeBlock(client, payout)
	if err != nil {
		t.Fatal(err)
	}
	if height != 301 || len(block.Txs[0].Outputs) != 2 {
		t.Fatal("unexpected coinbase shape")
	}
	outputs := block.Txs[0].Outputs
	if outputs[0].Amount != fixture.CoinbaseValue || hex.EncodeToString(outputs[0].Payload) != hex.EncodeToString(payout.Payload) {
		t.Fatal("wrong miner reward/address")
	}
	if outputs[1].Amount != 50_000_000 || outputs[0].Amount+outputs[1].Amount != 5_000_001_000 {
		t.Fatal("wrong treasury or fee accounting")
	}
	if err = consensus.CheckTreasury(consensus.MainNet, block.Txs[0], 301); err != nil {
		t.Fatal(err)
	}
	decoded, err := consensus.DecodeBlock(block.Serialize())
	if err != nil {
		t.Fatal(err)
	}
	if decoded.Header.Hash() != block.Header.Hash() {
		t.Fatal("block serialization changed header")
	}
	t.Log("authorized payout 49.50001 ARG; treasury 0.5 ARG; no extra payout or referral")
}

func TestGPUBlockAcceptedByOfficialConsensusOffline(t *testing.T) {
	exe := os.Getenv("ARGON_GPU_WORKER")
	if exe == "" {
		t.Fatal("ARGON_GPU_WORKER is required")
	}
	worker, err := startWorker(exe, 64)
	if err != nil {
		t.Fatal(err)
	}
	defer worker.close()
	ch, err := chain.Open(consensus.RegTest, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer ch.Close()
	pool := mempool.New(ch, mempool.DefaultConfig())
	payout, err := wallet.DecodeAddress(consensus.MainNet.HRP, "arg1qt8m4tkg5a2yvxlqumdvvuqza8nekrfzk8dhu6cnh5af9cy55atsq7zefrm")
	if err != nil {
		t.Fatal(err)
	}
	block, err := miner.BuildTemplate(ch, pool, payout, []byte("OFFLINE-GPU-REGTEST"), time.Now().Unix())
	if err != nil {
		t.Fatal(err)
	}
	result, err := worker.run(block.Header.Serialize(), 0)
	if err != nil {
		t.Fatal(err)
	}
	target, err := consensus.CompactToTarget(block.Header.Bits)
	if err != nil {
		t.Fatal(err)
	}
	for i, digest := range result.Hashes {
		b, err := hex.DecodeString(digest)
		if err != nil {
			t.Fatal(err)
		}
		var h consensus.Hash
		copy(h[:], b)
		if consensus.HashToBig(h).Cmp(target) > 0 {
			continue
		}
		block.Header.Nonce = uint64(i)
		if err = checkDigest(block.Header.Serialize(), uint64(i), digest); err != nil {
			t.Fatal(err)
		}
		serialized := block.Serialize()
		parsed, err := consensus.DecodeBlock(serialized)
		if err != nil {
			t.Fatal(err)
		}
		changed, err := ch.ProcessBlock(parsed, time.Now().Unix())
		if err != nil {
			t.Fatal(err)
		}
		if !changed || ch.Tip().Height != 1 {
			t.Fatal("GPU block did not advance official chain")
		}
		t.Logf("OFFLINE REGTEST GPU BLOCK ACCEPTED: height=1 hash=%s nonce=%d", block.Header.Hash(), i)
		return
	}
	t.Fatal("no easy-regtest target hit in 64 GPU attempts")
}

