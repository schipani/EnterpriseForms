SEC-BL-01 G08 delivery A/B/A (scratch clone; backlogs 32,256; WP5BDPlusDeliveryMeasurement).
A = the G08 migration additionally drops its two triggers (grants unchanged); B = G08 as implemented. Runs 1 and 3 (A) failed to build
(a broken string edit in the A variant, no measurement); the A variant was fixed and runs 5-7 repeated it. Results (backlog, entries/s, store ops/entry; steady p50 ms):
2-B-g08 B exit 0 [(32, 9.3, 5.219), (256, 10.7, 5.246)] steady p50 471.8
4-B-g08 B exit 0 [(32, 8.9, 5.219), (256, 10.6, 5.246)] steady p50 477.1
5-A-no-triggers A exit 0 [(32, 8.8, 5.219), (256, 10.6, 5.246)] steady p50 475.1
6-B-g08 B exit 0 [(32, 9.2, 5.219), (256, 10.6, 5.246)] steady p50 483.6
7-A-no-triggers A exit 0 [(32, 9.4, 5.219), (256, 10.7, 5.246)] steady p50 480.6
