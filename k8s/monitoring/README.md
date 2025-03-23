# Monitoring Stack

This directory contains files for deploying a monitoring stack using Helm. The stack includes Prometheus, Grafana, and Loki for comprehensive monitoring and logging.

## Files

- `install-monitoring.sh`: Script to install the entire monitoring stack using Helm
- `grafana-values.yaml`: Configuration values for Grafana Helm chart
- `prometheus.yaml`: Legacy Prometheus deployment (kept for reference)
- `prometheus-config.yaml`: Legacy Prometheus configuration (kept for reference)
- `grafana.yaml`: Legacy Grafana deployment (kept for reference)

## Usage

The monitoring stack can be installed by running:

```bash
./install-monitoring.sh
```

This script will:
1. Check if Helm is installed and install it if needed
2. Add the necessary Helm repositories
3. Install Prometheus, Loki, and Grafana
4. Configure Grafana with datasources for Prometheus and Loki
5. Add a sample Kubernetes dashboard

## Architecture

The monitoring stack consists of:

- Prometheus via the `kube-prometheus-stack` Helm chart: Collects and stores metrics
- Loki via the `loki-stack` Helm chart: Collects and stores logs
- Grafana: Visualizes metrics and logs from both sources

## Accessing Monitoring

After deployment, Grafana is available at: http://LOAD_BALANCER_IP:3000

- Username: admin
- Password: Retrieved automatically in the deployment script output 