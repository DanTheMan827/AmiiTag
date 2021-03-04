//
//  NFCMiFareTag+checkPuck.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/3/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import CoreNFC

extension NFCMiFareTag {
    func checkPuck(completionHandler: @escaping (Result<Data, Error>) -> Void) {
        sendMiFareCommand(commandPacket: Data([0x3A, 133, 134])) { (data, error) in
            if let error = error {
                completionHandler(.failure(error))
            } else if data.elementsEqual([1, 2, 3, 4, 5, 6, 7, 8]) {
                completionHandler(.success(data))
            } else {
                completionHandler(.failure(NFCMiFareTagError.unknownError))
            }
        }
    }
}
