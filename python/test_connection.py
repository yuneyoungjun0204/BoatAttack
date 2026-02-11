#!/usr/bin/env python3
"""
Quick Connection Test
Unity 빌드와 연결 테스트 (10초)
"""

import socket
import time

def test_connection(host='localhost', port=9876):
    print("=== Unity Connection Test ===")
    print(f"Connecting to {host}:{port}...")
    
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(5)
        sock.connect((host, port))
        
        print("✅ CONNECTION SUCCESS!")
        print("Unity is ready for RL training!")
        
        # 간단한 핑 테스트
        print("\nSending test message...")
        test_msg = '{"command": "ping"}\n'
        sock.sendall(test_msg.encode('utf-8'))
        
        print("Waiting for response...")
        time.sleep(1)
        
        if sock.recv(1024, socket.MSG_DONTWAIT):
            print("✅ Received response from Unity!")
        else:
            print("⚠️  No response (this is OK if Unity doesn't echo)")
        
        sock.close()
        print("\n✅ Test Complete! You can start training.")
        return True
        
    except socket.timeout:
        print("❌ CONNECTION TIMEOUT")
        print("Unity is not responding. Check:")
        print("  1. Unity build is running")
        print("  2. SocketServer is added to scene")
        print("  3. Port 9876 is not blocked")
        return False
        
    except ConnectionRefusedError:
        print("❌ CONNECTION REFUSED")
        print("Unity is not listening on port 9876. Check:")
        print("  1. Unity build is running")
        print("  2. SocketServer component is active")
        print("  3. Auto Start is enabled")
        return False
        
    except Exception as e:
        print(f"❌ ERROR: {e}")
        return False

if __name__ == '__main__':
    success = test_connection()
    exit(0 if success else 1)
