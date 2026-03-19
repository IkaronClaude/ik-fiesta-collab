#!/bin/sh
# Generate config.json from API_URL env var, then start nginx.
cat > /usr/share/nginx/html/config.json <<EOF
{"apiUrl":"${API_URL:-http://localhost:5000}"}
EOF
exec "$@"
