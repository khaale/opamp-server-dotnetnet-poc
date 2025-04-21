docker run --rm -v $(pwd)/config.yaml:/etc/otelcol-contrib/config.yaml --network host otel/opentelemetry-collector-contrib:latest
