// Command tsproxy runs an embedded Tailscale node (tsnet) for Camera Viewer.
//
// It joins the tailnet in userspace (no TUN device, no admin rights, no system Tailscale install) and exposes
// the node on a loopback address as:
//
//   - a SOCKS5 proxy onto the tailnet (user "tsnet", password = proxyCred), which Camera Viewer uses to reach
//     NVRs by their Tailscale or subnet-routed addresses, and
//   - the Tailscale LocalAPI under /localapi/ (header "Sec-Tailscale: localapi", basic-auth password =
//     localApiCred), used for status, login and logout.
//
// On startup it prints one JSON line to stdout: {"addr":..., "proxyCred":..., "localApiCred":...}.
// It exits when stdin is closed, so it never outlives the Camera Viewer process that started it.
// An auth key, if any, is read from the TS_AUTHKEY environment variable (not argv, so it stays out of ps).
package main

import (
	"encoding/json"
	"flag"
	"io"
	"log"
	"os"
	"os/signal"
	"syscall"

	"tailscale.com/tsnet"
)

func main() {
	dir := flag.String("dir", "", "directory for the node's state (keys, prefs)")
	hostname := flag.String("hostname", "camera-viewer", "machine name on the tailnet")
	flag.Parse()
	if *dir == "" {
		log.Fatal("-dir is required")
	}
	log.SetFlags(log.LstdFlags | log.Lmicroseconds)

	srv := &tsnet.Server{
		Dir:      *dir,
		Hostname: *hostname,
		AuthKey:  os.Getenv("TS_AUTHKEY"),
		Logf:     log.Printf, // verbose backend logs → stderr
		UserLogf: log.Printf,
	}
	if err := srv.Start(); err != nil {
		log.Fatalf("start: %v", err)
	}

	addr, proxyCred, localAPICred, err := srv.Loopback()
	if err != nil {
		srv.Close()
		log.Fatalf("loopback: %v", err)
	}
	if err := json.NewEncoder(os.Stdout).Encode(map[string]string{
		"addr":         addr,
		"proxyCred":    proxyCred,
		"localApiCred": localAPICred,
	}); err != nil {
		srv.Close()
		log.Fatalf("handshake: %v", err)
	}

	done := make(chan struct{})
	go func() {
		// Camera Viewer keeps our stdin open for its lifetime; EOF means it has gone away.
		io.Copy(io.Discard, os.Stdin)
		close(done)
	}()
	signals := make(chan os.Signal, 1)
	signal.Notify(signals, os.Interrupt, syscall.SIGTERM)

	select {
	case <-done:
		log.Printf("parent closed stdin; shutting down")
	case s := <-signals:
		log.Printf("received %v; shutting down", s)
	}
	srv.Close()
}
